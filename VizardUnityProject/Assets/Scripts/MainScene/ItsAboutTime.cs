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
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VizProtobufferMessage;
/// <summary>
/// Handles setting the current vizMessage to be displayed, also sets
/// the playback rate (real time or data rate), and updates the time
/// display text.
/// </summary>
public class ItsAboutTime : MonoBehaviour
{
    [Header("GUI Components (Playback Bar)")]
    public TextMeshProUGUI simTimeElapsedText;
    public TextMeshProUGUI playbackSpeedText;
    public GameObject realTimeLabel;
    public GameObject dataRateDisplay;
    public TextMeshProUGUI dataRateText;
    public Button playPauseButton;
    public TextMeshProUGUI timeButtonText;
    public Image playImage;
    public Image pauseImage;
    [Header("GUI Components (Time Menu)")]
    public Toggle dataRateDisplayToggle;
    public GameObject fpsDisplay;
    public Toggle display24HrToggle;
    public Toggle toggleRealTimePlayback;
    public TMP_InputField goToIndexInput;

    private bool showSimElapsedTime = true; //False will show epoch time
    private bool runInRealTimeMode;
    private bool show24HrClock;

    private float playbackControlMultiplier = 1;
    private double playbackSpeed;
    private int fps;
    private bool firstCycle = true;

    private DateTime epoch;
    private DateTime systemRestartTime;
    private double simRestartSeconds;
    private DateTime missionMsgRolloverSystemTime;
    
    private readonly double playbackFilter = 0.95f;

    //Data rate playback variables
    private int unityFrameCount = 1;

    //Real time playback calculation variables
    private DateTime systemTimeAtCurrentMsgDisplayStart;
    private double simTimeOfCurrentMsg;
    // Seconds without a new message before a live stream reads as paused
    private const double LiveStreamIdleSeconds = 1.0;
    private bool archiveFractionSet;

    // Start is called before the first frame update
    void Start()
    {
        ApplyUserSettings();
        CalculateEpoch();
        goToIndexInput.onSubmit.AddListener(GoToMessage);
    }

    private void ApplyUserSettings()
    {
        VizMessage.Types.VizSettingsPb userSettings = MessageList.FirstMessage.Settings;
        if (userSettings != null)
        {
            if (userSettings.Show24HrClock == 1)
            {
                show24HrClock = true;
                display24HrToggle.isOn = true;
            }

            if (userSettings.ShowDataRateDisplay == 1)
            {
                dataRateDisplayToggle.isOn = true;
            }

            if (userSettings.ShowMissionTime == 1)
            {
                showSimElapsedTime = false;
            }
        }
    }

    public void TimePanelEnabled()
    {
        toggleRealTimePlayback.isOn = runInRealTimeMode;
    }

    private void CalculateEpoch()
    {
        VizMessage.Types.EpochDateTime vizEpoch = MessageList.FirstMessage.Epoch;
        if (vizEpoch != null)
        {
            try
            {
                Debug.LogFormat("{0}/{1}/{2} - {3}:{4}:{5}", vizEpoch.Year, vizEpoch.Month, vizEpoch.Day,
                    vizEpoch.Hours, vizEpoch.Minutes, vizEpoch.Seconds);
                epoch = new DateTime(vizEpoch.Year, vizEpoch.Month, vizEpoch.Day, vizEpoch.Hours, vizEpoch.Minutes,
                    (int) vizEpoch.Seconds);
            }
            catch
            {
                VizardGUISettings.UpdateErrorMessages(String.Format(
                    "Could not convert EpochDateTime message contents into System DateTime. User provided: {0}/{1}/{2} - {3}:{4}:{5}. Set epoch to BSK default.",
                    vizEpoch.Year, vizEpoch.Month, vizEpoch.Day, vizEpoch.Hours, vizEpoch.Minutes, vizEpoch.Seconds));
                epoch = new DateTime(2019, 1, 1, 0, 0, 0);
            }
        }
        else
        {
            //Set the epoch to the bsk default value
            epoch = new DateTime(2019, 1, 1, 0, 0, 0);
        }
    }

    void FixedUpdate()
    {
        if ((VizardGUISettings.AssetLoadingComplete) && (DataManager.FirstMessageDisplayed)
            && LiveSmoothing.Active)
        {
            //A live run: LiveSmoothing chooses the moment drawn (live or replay)
            LiveSmoothing.Apply(playbackControlMultiplier);
            if (!MessageList.PlaybackPaused)
            {
                UpdateFPSDisplay();
            }
            RefreshTimeDisplays(playbackSpeed);
        }
        else if ((VizardGUISettings.AssetLoadingComplete) && (DataManager.FirstMessageDisplayed))
        {
            if (DataManager.IsLiveSim && DataManager.DisplayMostRecentMessage)
            {
                CheckForEndOfAvailableMessagesAndSetNextIndex(MessageList.TimestepsTotal - 1);
            }
 
            double newPlaybackSpeed = playbackSpeed;
            if (!MessageList.PlaybackPaused)
            {
                if (runInRealTimeMode)
                {
                    RealTimePlaybackUpdate();
                }
                else
                {
                    newPlaybackSpeed = DataRatePlaybackUpdate();
                }
                UpdateFPSDisplay();
            }
            RefreshTimeDisplays(newPlaybackSpeed);
        }
        else
        {
            LiveSmoothing.Reset();
            MessageList.CurrentIndex = 0;
            EnablePlaybackControls(false);
        }

        if (VizardGUISettings.StartupCount < 10)
        {
            VizardGUISettings.StartupCount++;
            if (VizardGUISettings.StartupCount == 10)
            {
                VizardGUISettings.PopRemoteAssetLoadFromList("",false);
            }
        }
    }

    private void RefreshTimeDisplays(double newPlaybackSpeed)
    {
        //Either mode will require elapsed time and epoch updates:
        if (showSimElapsedTime)
        {
            UpdateSimElapsedTimeDisplay();
        }
        else
        {
            UpdateMissionDateTimeDisplay();
        }

        UpdatePlaybackSpeedDisplay(newPlaybackSpeed);
    }

    private double DataRatePlaybackUpdate()
    {
        double newPlaybackSpeed = playbackSpeed;
        if (playbackControlMultiplier >= 1)
        {
            CheckForEndOfAvailableMessagesAndSetNextIndex(MessageList.CurrentIndex +
                                                          (int) playbackControlMultiplier);
            newPlaybackSpeed = TimeMsgUpdate();
        }
        else
        {
            float unityFramesToNextIndex = 1f / playbackControlMultiplier;
            unityFrameCount += 1;
            if (unityFrameCount >= unityFramesToNextIndex)
            {
                unityFrameCount = 0;
                CheckForEndOfAvailableMessagesAndSetNextIndex(MessageList.CurrentIndex + 1);
                newPlaybackSpeed = TimeMsgUpdate();
            }
        }

        return newPlaybackSpeed;
    }

    private void RealTimePlaybackUpdate()
    {
        TimeSpan systemInterval = DateTime.Now - systemRestartTime;
        double systemSecondsElapsed = systemInterval.TotalSeconds;
        double simSecondsElapsed = MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / (1e9) -
                                   simRestartSeconds;
        if (systemSecondsElapsed * playbackControlMultiplier >= simSecondsElapsed)
        {
            CheckForEndOfAvailableMessagesAndSetNextIndex(MessageList.CurrentIndex + 1);
            missionMsgRolloverSystemTime = DateTime.Now;
        }

        if (archiveFractionSet)
        {
            ResetSystemAndSimStartTimes();
            archiveFractionSet = false;
        }
    }

    public void EnablePlaybackControls(bool controlsOn, bool stayPaused = false)
    {
        playPauseButton.gameObject.SetActive(controlsOn);
        if (controlsOn)
        {
            if (!stayPaused)
            {
                SetupResumePlayback();
            }
        }
        else
        {
            SetupPausePlayback();
            playbackSpeedText.text = "PAUSED";
        }
    }

    private double TimeMsgUpdate()
    {
        // No new message since the last frame (a live stream between updates,
        // paused or ended): keep the last speed. Counting it as zero dragged the
        // filtered speed towards 0, and the readout into ever-growing "1/Nx"
        // values, whenever the simulation paused or finished.
        if (MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / 1e9 == simTimeOfCurrentMsg)
        {
            return playbackSpeed;
        }

        TimeSpan elapsedSystemTime = DateTime.Now - systemTimeAtCurrentMsgDisplayStart;
        double elapsedSimTime = MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / 1e9 - simTimeOfCurrentMsg;
        systemTimeAtCurrentMsgDisplayStart = DateTime.Now;
        simTimeOfCurrentMsg = MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / 1e9;
        if ((elapsedSimTime < 0) || (elapsedSystemTime.TotalSeconds <= 0))
        {
            return playbackSpeed;
        }

        return elapsedSimTime / elapsedSystemTime.TotalSeconds;
    }

    public void IncreasePlaybackSpeed()
    {
        if (!MessageList.PlaybackPaused)
        {
            playbackControlMultiplier *= 2f;
            VizardCommandLog.Log("speed_up", $"multiplier={VizardCommandLog.Value(playbackControlMultiplier)}");
            UpdateDataRateDisplay();
            if (runInRealTimeMode)
            {
                ResetSystemAndSimStartTimes();
            }
        }
    }

    public void DecreasePlaybackSpeed()
    {
        if (!MessageList.PlaybackPaused)
        {
            playbackControlMultiplier /= 2f;
            VizardCommandLog.Log("slow_down", $"multiplier={VizardCommandLog.Value(playbackControlMultiplier)}");
            UpdateDataRateDisplay();
            if (runInRealTimeMode)
            {
                ResetSystemAndSimStartTimes();
            }
        }
    }

    private void SetupResumePlayback()
    {
        playImage.transform.gameObject.SetActive(false);
        pauseImage.transform.gameObject.SetActive(true);
        playPauseButton.targetGraphic = pauseImage;
        ResetSystemAndSimStartTimes();
        MessageList.PlaybackPaused = false;
        realTimeLabel.SetActive(true);
        UpdateDataRateDisplay();
    }

    private void SetupPausePlayback()
    {
        playImage.transform.gameObject.SetActive(true);
        pauseImage.transform.gameObject.SetActive(false);
        playPauseButton.targetGraphic = playImage;
        MessageList.PlaybackPaused = true;
        realTimeLabel.SetActive(false);
        UpdateDataRateDisplay();
    }

    public void TogglePlaybackPause()
    {
        if (VizardGUISettings.AssetLoadingComplete)
        {
            VizardCommandLog.Log(MessageList.PlaybackPaused ? "play" : "pause");
            if (MessageList.PlaybackPaused)
            {
                SetupResumePlayback();
            }
            else
            {
                LiveSmoothing.PausedByViewer();     //A live run: play resumes from here, behind live
                SetupPausePlayback();
            }
        }
    }

    public void ApplyLiveSettingsPause()
    {
        SetupPausePlayback();
    }

    private void UpdateSimElapsedTimeDisplay()
    {
        double currentSimElapsedSeconds =
            MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / (1e9); //Gets sim time in seconds

        int simDays = (int) currentSimElapsedSeconds / 86400;
        string simDayString = simDays.ToString();
        if (simDays < 10)
        {
            simDayString = "0" + simDayString;
        }

        //Calculate how many hours in day fraction:
        int simHours = (int) (currentSimElapsedSeconds - simDays * 86400) / 3600;
        string simHourString = simHours.ToString();
        if (simHours < 10)
        {
            simHourString = "0" + simHourString;
        }

        //Calculate how many minutes in hour fraction:
        int simMins = (int) (currentSimElapsedSeconds - simDays * 86400 - simHours * 3600) / 60;
        string simMinuteString = simMins.ToString();
        if (simMins < 10)
        {
            simMinuteString = "0" + simMinuteString;
        }

        //Calculate how many seconds in minute fraction:
        double simSecs = currentSimElapsedSeconds - simDays * 86400 - simHours * 3600 - simMins * 60;
        string simSecondString = simSecs.ToString("F1");
        if (simSecs < 10)
        {
            simSecondString = "0" + simSecondString;
        }

        simTimeElapsedText.text = $"Sim Time Elapsed: {simDayString}:{simHourString}:{simMinuteString}:{simSecondString}";
    }

    private void UpdateMissionDateTimeDisplay()
    {
        double currentSimElapsedSeconds =
            MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / (1e9); //time in seconds
        if (!MessageList.PlaybackPaused)
        {
            //LiveSmoothing's frames already carry a smoothly advancing time: no extrapolation
            if (runInRealTimeMode && !LiveSmoothing.Active)
            {
                TimeSpan systemInterval = DateTime.Now - missionMsgRolloverSystemTime;
                double systemSecondsElapsed = systemInterval.TotalSeconds;
                DateTime currentMissionTime =
                    epoch.AddSeconds(currentSimElapsedSeconds + systemSecondsElapsed * playbackControlMultiplier);
                simTimeElapsedText.text = CreateEpochDisplayString(currentMissionTime);
            }
            else
            {
                DateTime currentMissionTime = epoch.AddSeconds(currentSimElapsedSeconds);
                simTimeElapsedText.text = CreateEpochDisplayString(currentMissionTime);
            }
        }
    }

    private string CreateEpochDisplayString(DateTime missionTime)
    {
        string displayString;
        if (show24HrClock)
        {
            displayString = "Mission Time: " + missionTime.ToString("yyyy MMM dd  HH:mm:ss");
        }
        else
        {
            displayString = "Mission Time: " + missionTime.ToString("yyyy MMM dd  hh:mm:ss tt");
        }

        return displayString;
    }

    private void UpdatePlaybackSpeedDisplay(double newSpeed)
    {
        if (LiveSmoothing.Active)
        {
            playbackSpeedText.text = LiveSmoothing.Readout();      //LIVE, or how far behind and at what speed
            return;
        }

        // A live simulation that has stopped sending (paused or finished)
        if (DataManager.IsLiveSim && !runInRealTimeMode &&
            (DateTime.Now - systemTimeAtCurrentMsgDisplayStart).TotalSeconds > LiveStreamIdleSeconds)
        {
            playbackSpeedText.text = "PAUSED";
            return;
        }

        if (!MessageList.PlaybackPaused)
        {
            double filteredPlaybackSpeed = CalculatePlaybackSpeed(newSpeed);
            playbackSpeed = filteredPlaybackSpeed;
            if (playbackSpeed >= 1)
            {
                if (playbackSpeed >= 100)
                {
                    playbackSpeedText.text = playbackSpeed.ToString("F0") + "x";
                }
                else
                {
                    playbackSpeedText.text = playbackSpeed.ToString("F1") + "x";
                }
            }
            else
            {
                float denominator = (float) (1 / playbackSpeed);
                if ((int) denominator == 1)
                {
                    playbackSpeedText.text = "1x";
                }
                else
                {
                    playbackSpeedText.text = "1/" + (int) denominator + "x";
                }
            }
        }
        else
        {
            playbackSpeedText.text = "PAUSED";
        }
    }

    private double CalculatePlaybackSpeed(double newSpeed)
    {
        double filteredPlaybackSpeed;
        if (runInRealTimeMode)
        {
            filteredPlaybackSpeed = playbackControlMultiplier;
        }
        else
        {
            filteredPlaybackSpeed = playbackFilter * playbackSpeed + (1 - playbackFilter) * newSpeed;
            if (firstCycle)
            {
                firstCycle = false;
                return newSpeed;
            }
        }

        return filteredPlaybackSpeed;
    }


    public void ToggleTimeDisplay()
    {
        showSimElapsedTime = !showSimElapsedTime;
        timeButtonText.text = showSimElapsedTime ? "  Show Mission Time  ( t )" : "  Show Sim Time        ( t )";
    }

    private void CheckForEndOfAvailableMessagesAndSetNextIndex(int proposedNextIndex)
    {
        if (proposedNextIndex >= MessageList.TimestepsTotal)
        {
            if (DataManager.IsLiveSim)
            {

                MessageList.SetNextIndex(MessageList.TimestepsTotal-1);
                DataManager.DisplayMostRecentMessage = false;
                if (!MessageList.FollowLive)
                {
                    //Playback has caught up with the newest message: follow the stream again
                    MessageList.FollowLive = true;
                    VizardCommandLog.Log("live_resumed", "reason=caught_up");
                }
            }
            else
            {
                MessageList.SetNextIndex(0);
                if (runInRealTimeMode)
                {
                    //Reset the system and sim reference times
                    ResetSystemAndSimStartTimes();
                }
            }
        }
        else
        {
            MessageList.SetNextIndex(proposedNextIndex);
        }
    }

    private void ResetSystemAndSimStartTimes()
    {
        systemRestartTime = DateTime.Now;
        simRestartSeconds = MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / (1e9);
        missionMsgRolloverSystemTime = DateTime.Now;
    }

    public void SetArchiveFraction(float fractionValue)
    {
        int nextIndex = Mathf.FloorToInt(fractionValue * MessageList.TimestepsTotal);
        if (nextIndex >= MessageList.TimestepsTotal)
        {
            nextIndex = MessageList.TimestepsTotal - 1;
        }
        else if (nextIndex <= 0)
        {
            nextIndex = 0;
        }

        MessageList.SetNextIndex(nextIndex, true);
        if (runInRealTimeMode)
        {
            archiveFractionSet = true;
            ResetSystemAndSimStartTimes();
        }
        
        MessageList.SetNextIndex(nextIndex, true);
    }

    public float GetArchiveFraction()
    {
        // Prevent jitter of slider when new frames are added but the current index isn't immediately updated.
        // Following a live run (drawn from LiveSmoothing's buffer, a little behind): at the end.
        if (DataManager.IsLiveSim && (MessageList.FollowLive || MessageList.CurrentIndex + 2 >= MessageList.TimestepsTotal))
        {
            return 1;
        }

        float tempIndex = MessageList.CurrentIndex;
        float totalIndices = MessageList.TimestepsTotal;
        float archiveFraction = tempIndex / totalIndices;

        return archiveFraction;
    }

    public void TogglePlaybackMode(bool realTimeOn)
    {
        VizardCommandLog.Log("playback_mode", realTimeOn ? "mode=real_time" : "mode=data_rate");
        if (realTimeOn)
        {
            dataRateDisplay.SetActive(false);
            ResetSystemAndSimStartTimes();
            playbackControlMultiplier = 1;
            runInRealTimeMode = true;
        }
        else
        {
            dataRateDisplay.SetActive(dataRateDisplayToggle.isOn);

            playbackControlMultiplier = 1;
            runInRealTimeMode = false;
        }

        UpdateDataRateDisplay();
    }

    public void SetPlaybackControlMultiplier(int pow)
    {
        playbackControlMultiplier = Mathf.Pow(2, pow);
        UpdateDataRateDisplay();
        if (runInRealTimeMode)
        {
            ResetSystemAndSimStartTimes();
        }
    }

    public void Toggle24HrClockDisplay(bool use24Hr)
    {
        show24HrClock = use24Hr;
    }

    public void ToggleDataRateDisplay(bool showDataRate)
    {
        if (showDataRate)
        {
            if (!runInRealTimeMode)
            {
                dataRateDisplay.SetActive(true);
                UpdateDataRateDisplay();
            }
        }
        else
        {
            dataRateDisplay.SetActive(false);
        }
    }

    public void ToggleFPSDisplay(bool showFPS)
    {
        if (showFPS)
        {
            if (!runInRealTimeMode)
            {
                fpsDisplay.SetActive(true);
                UpdateFPSDisplay();
            }
        }
        else
        {
            fpsDisplay.SetActive(false);
        }
    }

    private void UpdateDataRateDisplay()
    {
        if (dataRateDisplay.activeSelf)
        {
            if ((!runInRealTimeMode) && (!MessageList.PlaybackPaused))
            {
                if (playbackControlMultiplier < 1)
                {
                    dataRateText.text = $"1/{(int) (1 / playbackControlMultiplier)} msg / frame";
                }
                else
                {
                    dataRateText.GetComponent<TextMeshProUGUI>().text =
                        $"{playbackControlMultiplier} msg / frame";
                }
            }
            else
            {
                dataRateText.GetComponent<TextMeshProUGUI>().text = "";
            }
        }
    }

    private void UpdateFPSDisplay()
    {
        if (fpsDisplay.activeSelf)
        {
            if (fps == 0)
            {
                fps = (int) (1f / Time.unscaledDeltaTime);
            }
            else
            {
                int newFPS = (int) (1f / Time.unscaledDeltaTime);
                fps = (int) (playbackFilter * fps + newFPS * (1 - playbackFilter));
            }

            //Add a low pass filter
            fpsDisplay.GetComponent<Text>().text = fps.ToString() + "  FPS";
        }
    }

    public void GoToMessage(string goToID)
    {
        VizardCommandLog.Log("go_to_message", $"to={VizardCommandLog.Value(goToID)}");
        int messageID = int.Parse(goToID);
        if ((messageID > 0) && (messageID <= MessageList.TimestepsTotal))
        {
            MessageList.SetNextIndex(messageID - 1, true);
            if (MessageList.PlaybackPaused)
            {
                MessageList.SetNextIndex(messageID - 1, true);
            }
        }
    }

    public string VR_RadialMenuGetPlaybackSpeed()
    {
        if (!MessageList.PlaybackPaused)
        {
            if (playbackSpeed >= 1)
            {
                return playbackSpeed.ToString("F0") + "x";
            }

            float denominator = (float) (1 / playbackSpeed);
            if ((int) denominator == 1)
            {
                return "1x";
            }

            return "1/" + (int) denominator + "x";
        }

        return "PAUSED";
    }
}
