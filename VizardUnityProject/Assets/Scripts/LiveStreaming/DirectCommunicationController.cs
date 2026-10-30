/*
 ISC License

 Copyright (c) 2025, Autonomous Vehicle Systems Lab, University of Colorado at Boulder

 Permission to use, copy, modify, and/or distribute this software for any
 purpose with or without fee is hereby granted, provided that the above
 copyright notice and this permission notice appear in all copies.

 THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

 */

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Google.Protobuf;
using VizProtobufferMessage;

// NetMQ needs System.Net.Sockets and background threads, neither of which exists
// in a WebGL player, so the ZMQ path is compiled out of browser builds. It stays
// available in the Editor regardless of the selected build target so that
// desktop development is unaffected while targeting WebGL.
#if !UNITY_WEBGL 
using NetMQ;
#endif

/// <summary>
/// Handles communication with live Basilisk simulation
/// <remarks>Basilisk can be reached either directly over ZMQ (desktop builds)
/// or through the Python WebSocket bridge in bridge/bsk_viz_bridge.py, which is
/// the only option available to a WebGL build. The transport is chosen by the
/// scheme of the address the user supplies: ws:// or wss:// selects the bridge,
/// anything else is treated as a ZMQ address.</remarks>
/// </summary>
public class DirectCommunicationController : MonoBehaviour
{
#if !UNITY_WEBGL
    private ResSocket resSocket; //Response socket, used for two-way communication
    private SubSocket subSocket; //Subscribe socket, used for receive only communication
#endif
    private IVizTransport bridgeTransport; //Set when connected through the WebSocket bridge instead of ZMQ
    private VizInputAccumulator vizInputs; //Vizard user inputs to live Basilisk sim to be communicated in next message

    private const float InputSendInterval = 0.05f; //Seconds between user input pushes over the bridge

    //New-run reset, requested over the socket with "RESET" (see RequestNewRun)
    private readonly object newRunGate = new object(); //Guards the two fields below across socket and main threads
    private bool awaitingNewRun; //RESET received: hold the new run's messages until its first one arrives
    private readonly Queue<VizMessage> newRunMessages = new Queue<VizMessage>(); //The held messages
    private float lastInputSentTime; //Unscaled time of the last user input push

    //Camera change, requested over the socket with "CAMERA <name>" (see HandleCameraCommand).
    //"CAMERA" with no name returns to the run's starting target (MainCameraUtilities.SceneDefaultTarget).
    private volatile string pendingCameraTarget;

    //Timing data for measuring livestreaming metrics
    private DateTime imageRequestStartTime; //System time image request received from Basilisk sim

    private DateTime
        imageTransmitStartTime; //System time Vizard began to transmit the requested image back to Basilisk sim

    private DateTime imageTransmitEndTime; //System time Vizard finished transmitting the requested image

    public List<string> messageSubscriptions = new List<string>(); //Message types that socket is subscribed to

    /// <summary>
    /// State of the bridge connection, or null when Basilisk is reached over ZMQ
    /// (or no connection has been started). ZMQ cannot report whether anyone is
    /// listening, so only the bridge has a state worth exposing.
    /// </summary>
    public VizTransportState? BridgeState => bridgeTransport?.State;

    /// <summary>Why the bridge connection failed, or null.</summary>
    public string BridgeError => bridgeTransport?.LastError;

    void Awake()
    {
        DontDestroyOnLoad(this.gameObject); //Keep this instance alive from StartupScene to be used in Main Scene
        vizInputs = this.GetComponent<VizInputAccumulator>(); //Track Vizard user inputs to be communicated to Basilisk
    }

    /// <summary>
    /// Connect the Event Dialog Manager to the DirectComm controller
    /// to allow user choices on Event Dialogs to be communicated to Basilisk
    /// </summary>
    /// <param name="eventDialogMgr">Vizard Main Scene instance of EventDialogManager</param>
    public void ConnectEventDialogManager(EventDialogManager eventDialogMgr)
    {
        vizInputs.eventDialogManager = eventDialogMgr;

        //If the socket is for two-way communication, set any Basilisk specified hot keys 
        //that should be listened for and reported back to Basilisk
        if (!DataManager.SocketIsReceiveOnly)
        {
            VizProtobufferMessage.VizMessage.Types.VizSettingsPb settings = MessageList.FirstMessage.Settings;
            if (settings != null)
            {
                vizInputs.SetListenerStringForKeyboard(settings.KeyboardLiveInput);
            }
        }
    }

    /// <summary>
    /// Start the correct socket for the type of streaming communication chosen by the user
    /// </summary>
    /// <param name="address">Socket address to connect Vizard</param>
    /// <returns></returns>
    public bool StartCommunication(string address)
    {
        //The bridge normalizes both Basilisk streaming modes into a single
        //inbound stream, so the receive-only/two-way split does not apply to it.
        if (VizWebSocketTransport.IsWebSocketAddress(address))
        {
            bridgeTransport = new VizWebSocketTransport(address);
            return bridgeTransport.Connect();
        }

#if !UNITY_WEBGL
        if (DataManager.SocketIsReceiveOnly) //Set up receive only socket
        {
            //Add only the NetMQ message types that can be handled in receive only
            messageSubscriptions.Add("SIM_UPDATE");
            messageSubscriptions.Add("SYNC_SETTINGS");
            //Create the subscription socket
            subSocket = new SubSocket(ReceiveVizMessageSubSocket, address, messageSubscriptions);
            //Start the socket
            return subSocket.Start();
        }

        //Create the response socket for two-way communication
        resSocket = new ResSocket(address, RequestCallback);
        //Start the socket
        return resSocket.Start();
#else
        Debug.LogError(
            $"'{address}' is not a WebSocket address. A browser build cannot speak ZMQ, so it must " +
            "connect through the bridge instead, for example ws://127.0.0.1:8765.");
        return false;
#endif
    }

    /// <summary>
    /// Pump the bridge transport and hand over anything it has received.
    /// <remarks>Deliberately done here on the main thread. The ZMQ sockets
    /// deliver messages on their own listener threads, which means they touch
    /// Vizard state off the main thread; the bridge transport queues bytes
    /// instead and they are decoded here, where that is safe.</remarks>
    /// </summary>
    private void Update()
    {
        HandleNewRun();
        HandleCameraCommand();
        ScarletPresentation.Poll(); //A pending "FAULTS" request

        if (bridgeTransport == null)
        {
            return;
        }

        bridgeTransport.Poll();

        while (bridgeTransport.TryDequeue(out VizPayload payload))
        {
            switch (payload.Type)
            {
                case VizPayloadType.SimUpdate:
                    HandleSimUpdate(payload.Data);
                    break;
                case VizPayloadType.SyncSettings:
                    HandleSyncSettings(payload.Data);
                    break;
            }
        }

        //In two-way mode Basilisk asks for accumulated user input each step. The
        //bridge answers immediately with whatever it last received, so input is
        //pushed up as it happens rather than pulled on request. Only the newest
        //input is ever used, so pushing at the render framerate would just waste
        //bandwidth.
        if (bridgeTransport.State == VizTransportState.Connected
            && !DataManager.SocketIsReceiveOnly
            && vizInputs != null
            && Time.unscaledTime - lastInputSentTime >= InputSendInterval)
        {
            lastInputSentTime = Time.unscaledTime;
            VizInput inputResponse = vizInputs.GetInputResponseMessage();
            if (inputResponse != null)
            {
                bridgeTransport.SendInput(inputResponse.ToByteArray());
            }
        }
    }

    /// <summary>
    /// Stop communication with Basilisk by stopping the socket in use
    /// </summary>
    public void StopSocket()
    {
        if (bridgeTransport != null)
        {
            bridgeTransport.Close();
            bridgeTransport = null;
            return;
        }

#if !UNITY_WEBGL
        if (DataManager.SocketIsReceiveOnly)
        {
            subSocket.Stop();
        }
        else
        {
            resSocket.Stop();
        }

        NetMQConfig.Cleanup();
#endif
    }

    /// <summary>
    /// Handles a serialized VizMessage, whatever transport carried it.
    /// </summary>
    /// <param name="data">Serialized VizMessage protobuf</param>
    private void HandleSimUpdate(byte[] data)
    {
        VizMessage vizMessage = VizMessage.Parser.ParseFrom(data);
        //Add it to the message dictionary in MessageList
        MessageList.AddLiveMessage(vizMessage);
        //If the settings message has not been received yet and this message includes VizMessage.Settings
        if ((!MessageList.SettingsMessageReceived) && (vizMessage.Settings != null))
        {
            //Set the included Settings to be part of the first message in the dictionary
            MessageList.AddSettingsMessageToFirstMessage(vizMessage);
        }
    }

    /// <summary>
    /// Handles serialized broadcast sync settings, whatever transport carried them.
    /// </summary>
    /// <param name="data">Serialized VizBroadcastSyncSettings protobuf</param>
    private void HandleSyncSettings(byte[] data)
    {
        VizBroadcastSyncSettings syncSettings = VizBroadcastSyncSettings.Parser.ParseFrom(data);
        //Apply the latest sync settings to the broadcast viewer's Vizard instance
        MessageList.LatestBroadcastSyncSettings = syncSettings;
    }

    /// <summary>
    /// Called on the socket thread for a "RESET" request: the streaming application
    /// is about to start a new run. The old run stays on screen; the new run's
    /// messages are held back until its first one arrives (see HandleNewRun).
    /// </summary>
    private void RequestNewRun()
    {
        lock (newRunGate)
        {
            awaitingNewRun = true;
            newRunMessages.Clear();
        }
    }

    /// <summary>
    /// The body name in a "CAMERA" request, "CAMERA <name>" for a spacecraft or
    /// celestial body, "CAMERA" alone for the run's starting target. Empty
    /// string means default; unknown text is resolved (or reported) later.
    /// </summary>
    private static string CameraTargetFromRequest(string requestString)
    {
        int start = requestString.IndexOf("CAMERA", StringComparison.Ordinal);
        return start < 0 ? "" : requestString.Substring(start + 6).Trim();
    }

    /// <summary>
    /// Main-thread half of the app's "CAMERA" request: point the main camera at
    /// the named body (spacecraft or celestial), or back at the run's starting
    /// target when no name was given. No-op when the Main Scene is not up.
    /// </summary>
    private void HandleCameraCommand()
    {
        string target = pendingCameraTarget;
        if (target == null)
        {
            return;
        }
        pendingCameraTarget = null;
        if (MainCameraUtilities.MainCamera == null)
        {
            return; //Still on the Startup Scene: no camera to point yet
        }
        //Every target, spacecraft included, gets Vizard's own camera view of it,
        //which the presenter can orbit and zoom with the mouse as usual.
        //(ScarletPresentation's Earth-relative framing re-positioned the camera
        //every frame, so a framed spacecraft could not be orbited: not used.)
        ScarletPresentation.StopFraming();
        //"cluster": every spacecraft in view (ScarletPresentation.ViewCluster)
        if (target == "cluster")
        {
            if (!ScarletPresentation.ViewCluster())
            {
                VizardGUISettings.UpdateErrorMessages("Cluster view: no spacecraft in the scene yet.", true);
            }
            return;
        }
        string name = target.Length > 0 ? target : MainCameraUtilities.SceneDefaultTarget;
        if (string.IsNullOrEmpty(name))
        {
            return; //A scene without a starting target keeps Vizard's own choice
        }
        GameObject targetObject = CelestialBodyStateUtilities.GetGameObjectWithBodyName(name);
        if (targetObject == null)
        {
            VizardGUISettings.UpdateErrorMessages($"Camera target '{name}' is not in the scene.", true);
            return;
        }
        MainCameraUtilities.MainCamera.GetComponent<MainCameraViewManager>()
            .SetupChangeOfMainCameraTarget(targetObject);
    }

    /// <summary>
    /// A live message from the socket thread: held while a new run is pending,
    /// otherwise added to the message list as usual.
    /// </summary>
    private void AddLiveMessage(VizMessage vizMessage)
    {
        lock (newRunGate)
        {
            if (awaitingNewRun)
            {
                newRunMessages.Enqueue(vizMessage);
                return;
            }

            MessageList.AddLiveMessage(vizMessage);
        }
    }

    /// <summary>
    /// Main-thread half of the new-run reset. Once the new run's first message has
    /// arrived, tears the old run down, hands the held messages over and reloads the
    /// Main Scene, all in one frame -- the same order DataManager.LoadFile uses. The
    /// old scene is never left running over an empty message list.
    /// </summary>
    private void HandleNewRun()
    {
        lock (newRunGate)
        {
            if (!awaitingNewRun || newRunMessages.Count == 0)
            {
                return;
            }

            awaitingNewRun = false;
            //Still on the Startup Scene: no run is displayed and the Startup Scene
            //loads the Main Scene itself once messages arrive
            bool runDisplayed = SceneManager.GetActiveScene().name == DataManager.MainSceneToLoad;
            if (runDisplayed)
            {
                DataManager.ResetForNewLiveRun();
            }

            while (newRunMessages.Count > 0)
            {
                MessageList.AddLiveMessage(newRunMessages.Dequeue());
            }

            if (runDisplayed)
            {
                SceneManager.LoadScene(DataManager.MainSceneToLoad);
            }
        }
    }

#if !UNITY_WEBGL 
    /// <summary>
    /// Returns the correct response for a Basilisk request message
    /// </summary>
    /// <param name="request">Current Basilisk request message</param>
    /// <returns></returns>
    private NetMQMessage RequestCallback(NetMQMessage request)
    {
        NetMQMessage response = new NetMQMessage();
        //Parse the request to pull out the substring of the 
        //request type
        string requestString = "PING";
        if (request.FrameCount > 0)
        {
            requestString = request[0].ConvertToString();
        }
        string requestText = requestString; //As sent: "CAMERA <name>" carries its argument

        if (requestString.Contains("REQUEST_IMAGE"))
        {
            requestString = "REQUEST_IMAGE";
        }
        else if (requestString.Contains("REQUEST_INPUT"))
        {
            requestString = "REQUEST_INPUT";
        }
        else if (requestString.Contains("PING"))
        {
            requestString = "PING";
        }
        else if (requestString.Contains("SIM_UPDATE"))
        {
            requestString = "SIM_UPDATE";
        }
        else if (requestString.Contains("RESET"))
        {
            requestString = "RESET";
        }
        else if (requestString.Contains("CAMERA"))
        {
            requestString = "CAMERA";
        }
        else if (requestString.Contains("FAULTS"))
        {
            requestString = "FAULTS";
        }
        else if (requestString.Contains("SPACECRAFT_SIZE"))
        {
            requestString = "SPACECRAFT_SIZE";
        }
        else if (requestString.Contains("MODELS"))
        {
            requestString = "MODELS";
        }
        else if (requestString.Contains("PLAYBACK_RATE"))
        {
            requestString = "PLAYBACK_RATE";
        }
        else if (requestString.Contains("PLAYBACK_LIVE"))
        {
            requestString = "PLAYBACK_LIVE";
        }
        else if (requestString.Contains("PLAYBACK_SEEK"))
        {
            requestString = "PLAYBACK_SEEK";
        }

        //Take the correct action for the Basilisk request
        switch (requestString)
        {
            case "PING":
                //Keep the socket alive
                response.Append("PONG");
                break;
            case "SIM_UPDATE":
                //Receive a VizMessage containing an update on all the scenario objects in the scene
                ReceiveVizMessageResSocket(request);
                response.Append("OK");
                break;
            case "RESET":
                //The streaming application is about to start a new run on this stream
                RequestNewRun();
                response.Append("OK");
                break;
            case "PLAYBACK_RATE":
                //The demo app's simulation speed: replay plays the past at it (LiveSmoothing)
                response.Append(LiveSmoothing.SetRateFromRequest(requestText) ? "OK" : "ERROR");
                break;
            case "PLAYBACK_SEEK":
                //Replay from a sim time, as a scrub would (LiveSmoothing, main thread)
                response.Append(LiveSmoothing.SeekFromRequest(requestText) ? "OK" : "ERROR");
                break;
            case "PLAYBACK_LIVE":
                //The demo app's "Go live": jump to the live edge (LiveSmoothing, main thread)
                LiveSmoothing.RequestGoLive();
                response.Append("OK");
                break;
            case "FAULTS":
                //The demo app's faulted spacecraft: flashing fault rings (ScarletPresentation)
                ScarletPresentation.SetFaultsFromRequest(requestText);
                response.Append("OK");
                break;
            case "SPACECRAFT_SIZE":
                //The demo app's satellite size in planet local view (ScarletPresentation)
                response.Append(ScarletPresentation.SetSpacecraftSizeFromRequest(requestText) ? "OK" : "ERROR");
                break;
            case "MODELS":
                //The demo app's choice of model or sprite per spacecraft (ScarletPresentation)
                ScarletPresentation.SetModelsFromRequest(requestText);
                response.Append("OK");
                break;
            case "CAMERA":
                //The demo app asked for a different main camera target, by name:
                //applied on the main thread (HandleCameraCommand), like RESET
                pendingCameraTarget = CameraTargetFromRequest(requestText);
                response.Append("OK");
                break;
            case "REQUEST_INPUT":
                //Send all listened for user input that occurred since last request
                VizInput inputResponse = vizInputs.GetInputResponseMessage();
                response.Append("VIZARD_INPUT");
                response.Append(inputResponse.ToByteArray());
                break;
            case "REQUEST_IMAGE":
                //Take an image with the requested camera and 
                //stream the image back to Basilisk
                imageRequestStartTime = DateTime.Now;
                RequestImage(request);
                imageTransmitStartTime = DateTime.Now;

                AtomicImageBuffer.LockBuffer();
                response.Append(AtomicImageBuffer.ImageBuffer.Length);
                response.Append(AtomicImageBuffer.ImageBuffer);
                AtomicImageBuffer.ReleaseBuffer();
                imageTransmitEndTime = DateTime.Now;
                if (DataManager.SaveFPSMetricsToFile)
                {
                    TimeSpan renderInterval = imageTransmitStartTime - imageRequestStartTime;
                    double renderSeconds = renderInterval.TotalSeconds;
                    TimeSpan transmitInterval = imageTransmitEndTime - imageTransmitStartTime;
                    double timeToTransmit = transmitInterval.TotalSeconds;
                    DataManager.SaveMetrics($"{renderSeconds}, {timeToTransmit}");
                }

                break;
            default:
                response.Append("ERROR");
                break;
        }

        return response;
    }

    /// <summary>
    /// Basilisk sim has requested an image from an instrument camera (cameraID provided)
    /// for the most recent VizMessage received
    /// </summary>
    /// <param name="message">NetMQ image request message</param>
    private void RequestImage(NetMQMessage message)
    {
        //Set cameraID to message once that information is being sent in the message
        string requestString = message[0].ConvertToString();
        int cameraID = -1;
        if (requestString.Length > 13)
        {
            string cameraString = requestString.Substring(14);
            cameraID = Int32.Parse(cameraString);
        }

        //Do not advance playback of vizMessages until image has been rendered and transmitted
        MessageList.PlaybackPaused = true;
        //Set the state of the scenario objects to the most recent VizMessage
        MessageList.CurrentIndex = MessageList.TimestepsTotal - 1;
        //Set flag to request camera image for instrument camera matching cameraID
        AtomicImageBuffer.RequestScreenshot(cameraID);
    }
#endif

    /// <summary>
    /// Shut down communication and save accumulated VizMessages to file
    /// </summary>
    private void OnApplicationQuit()
    {
        if (!DataManager.IsLiveSim)
        {
            return;
        }

        if (bridgeTransport != null)
        {
            //A browser has no writable filesystem to save the run to, so the
            //accumulated messages are simply dropped on exit.
#if !UNITY_WEBGL 
            MessageList.SaveMessages("last_run.bin");
#endif
            bridgeTransport.Close();
            bridgeTransport = null;
            return;
        }

#if !UNITY_WEBGL 
        MessageList.SaveMessages("last_run.bin");

        if (!DataManager.SocketIsReceiveOnly)
        {
            resSocket.Stop();
        }
        else
        {
            if (subSocket != null)
            {
                subSocket.Stop();
            }
        }

        NetMQConfig.Cleanup();
#endif
    }

#if !UNITY_WEBGL 
    /// <summary>
    /// Handles receiving a VizMessage in two-way communication
    /// </summary>
    /// <param name="message">NetMQ SIM_UPDATE message</param>
    private void ReceiveVizMessageResSocket(NetMQMessage message)
    {
        //For backward compatibility, vizInterface is sending two empty frames between the header and the protobuffer message
        byte[] data = message[3].ToByteArray();
        //Parse the vizMessage from the third frame
        VizMessage vizMessage = VizMessage.Parser.ParseFrom(data);
        //Add it to the message dictionary in MessageList (or hold it for a pending new run)
        AddLiveMessage(vizMessage);
    }

    /// <summary>
    /// Handles receiving subscribed to messages from Basilisk in receive-only communication
    /// </summary>
    /// <param name="message">NetMQ SIM_UPDATE message</param>
    private void ReceiveVizMessageSubSocket(NetMQMessage message)
    {
        string messageTopicReceived = message[0].ConvertToString();
        //Basilisk broadcast socket doesn't bother with empty frames between header and payload
        if (messageTopicReceived == "SIM_UPDATE")
        {
            HandleSimUpdate(message[1].ToByteArray());
        }
        //If receiving settings that should be applied to keep broadcast viewers in-sync with the
        //current Vizard view settings of the instructor
        else if (messageTopicReceived == "SYNC_SETTINGS")
        {
            HandleSyncSettings(message[1].ToByteArray());
        }
    }
#endif
}