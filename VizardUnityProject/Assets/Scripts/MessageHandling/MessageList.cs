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
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using UnityEngine;
using VizProtobufferMessage;

/// <summary>
/// Maintains the dictionary of available VizMessages by loading
/// from playback file or by adding streamed messages. 
/// </summary>
public static class MessageList
{
    public static bool PlaybackPaused = false;

    /// <summary>
    /// Live streaming: true while the view follows the newest message. Scrubbing back
    /// turns it off, so the run keeps streaming into the buffer while the past is shown;
    /// it turns back on when playback catches up with the newest message, or the time
    /// slider is dragged to its end.
    /// </summary>
    public static bool FollowLive = true;

    private static string filepath;

    private static long bufferLimit = 10000000;
    private static int countToNextMessagePositionIndex = 10; 

    private static ConcurrentDictionary<int, VizMessage> messages = new ConcurrentDictionary<int, VizMessage>();

    private static List<long> messageFilePositions = new List<long>();

    private static int bufferStartIndex = -1;
    private static int bufferEndIndex = -1;

    private static int startMsgIndexToPlot;
    private static int endMsgIndexToPlot;
    public static int VisibleHistoryUpdateCount = 0;

    private static bool firstMessageAdded;

    public static int FirstMessageIndexOfPlottedMessages
    {
        get { return startMsgIndexToPlot; }
    }

    public static int LastMessageIndexOfPlottedMessages
    {
        get { return endMsgIndexToPlot; }
    }

    public static bool InBufferLoad;
    public static int DesiredNextIndex = -1;

    private static int timestepsTotal;

    public static int TimestepsTotal
    {
        get { return timestepsTotal; }
        set { timestepsTotal = value; }
    }

    public static int CurrentIndex { get; set; }

    private static VizMessage firstMessage;
    public static bool SettingsMessageReceived = true;
    private static VizMessage tempHoldMessage;
    public static VizMessage FirstMessage
    {
        get
        {
            if (firstMessage == null)
            {
                firstMessage = messages[0].Clone(); 
            }
            return firstMessage;
        }
    }

    public static double TimeStepSize { get; set; } //Assumes messages have a constant time step size

    /// <summary>
    /// While following a live run: the scene drawn between two frames (see
    /// LiveSmoothing). Null otherwise. Set on the main thread only.
    /// </summary>
    public static volatile VizMessage BlendedMessage;

    public static VizMessage CurrentMessage
    {
        get
        {
            if (DesiredNextIndex != -1)
            {
                return tempHoldMessage;
            }
            VizMessage blended = BlendedMessage;
            if (blended != null)
            {
                return blended;
            }
            return messages[CurrentIndex];
        }
    }

    /// <summary>A frame, if it is in memory (no buffer loading).</summary>
    public static bool TryGetMessage(int index, out VizMessage message)
    {
        return messages.TryGetValue(index, out message);
    }

    public static VizBroadcastSyncSettings LatestBroadcastSyncSettings { get; set; }

    public static void SetNextIndex(int nextIndexRequested, bool sliderMoved = false)
    {
        int nextIndex = nextIndexRequested;
        if ((DesiredNextIndex != -1) && (!sliderMoved))
        {
            //If waiting on a buffer load, try again to get the last message frame requested
            nextIndex = DesiredNextIndex;
        }

        if (nextIndex >= TimestepsTotal)
        {
            nextIndex = 0;
        }

        if (messages.Count == TimestepsTotal)
        {
            DesiredNextIndex = -1;
            CurrentIndex = nextIndex;
            
        }
        else
        {
            if (messages.ContainsKey(nextIndex))
            {
                DesiredNextIndex = -1;
                CurrentIndex = nextIndex;
                if (sliderMoved)
                {
                    SetVisibleHistoryRanges();
                }
            }
            else
            {
                DesiredNextIndex = nextIndex;
                LoadNextBuffer(nextIndex); //Add some logic to put the desired message in the middle of the buffer
                SetVisibleHistoryRanges();
            }
        }

        if (CurrentIndex == 0)
        {
            SetVisibleHistoryRanges();
        }
    }

    public static void AddMessage(int messageIndex, VizMessage message)
    {
	    if (!messages.TryAdd(messageIndex, message))
	    {
		    // see https://stackoverflow.com/questions/11501931/can-concurrentdictionary-tryadd-fail
		    // for conditions of TryAdd failure
		    Debug.LogError("Failed to add message to log.");
	    }
        if (!firstMessageAdded)
        {
            firstMessage = messages[0].Clone();
            firstMessageAdded = true;
        }
        
        if ((!DataManager.SaveMsgFileOnQuit) && (DataManager.InNoDisplayMode) && (messages.Count >= 20))
        {
            messages.TryRemove(TimestepsTotal - 20, out var lastMsg);
        }
    }

    public static void AddLiveMessage(VizMessage message)
    {
        AddMessage(timestepsTotal, message);
        DataManager.DisplayMostRecentMessage = FollowLive;
        timestepsTotal++;
        SetVisibleHistoryRanges();
    }

    private static void SetVisibleHistoryRanges()
    {
        if (messages.Count == timestepsTotal) 
        {
            startMsgIndexToPlot = 0;
            endMsgIndexToPlot = timestepsTotal - 1;
            //Happens during live streaming, count on the livestreaming catches in DrawTruePath to fix this
        }
        else
        {
            startMsgIndexToPlot = bufferStartIndex; 
            endMsgIndexToPlot = bufferEndIndex; 
            if (startMsgIndexToPlot != 0)
            {
                if (startMsgIndexToPlot > endMsgIndexToPlot) //contains EOF wrap
                {
                    int currentIndexToUse = CurrentIndex;
                    if (DesiredNextIndex != -1)
                    {
                        currentIndexToUse = DesiredNextIndex;
                    }

                    if (currentIndexToUse >= startMsgIndexToPlot)
                    {
                        endMsgIndexToPlot = timestepsTotal - 1;
                    }
                    else
                    {
                        startMsgIndexToPlot = 0;
                    }
                }
            }
            VisibleHistoryUpdateCount += 1; //Because buffer has rolled over
        }
        
    }

    public static bool FirstMessageBuffersReadFromFile(string filename, long testBufferSize = 0)
    {
        filepath = filename;
        if (!InBufferLoad)
        {
            ResetMessageListVariables();
            InBufferLoad = true;
            messages.Clear();
            messageFilePositions = new List<long>();
            timestepsTotal = 0;
            
            int messageReadAttempt = 0; // frames are indexed at 0

            using (FileStream msgFile = File.Open(filepath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bool firstMsg = true;
                while (msgFile.Position < msgFile.Length)
                {
                    try
                    {
                        //Save off evenly spaced positions in file for a
                        // total of "countOfFilePositionsIndexed" (to be used
                        // to place FileStream position when reading file)
                        if ((timestepsTotal % countToNextMessagePositionIndex) == 0) {
                            messageFilePositions.Add(msgFile.Position);
                        }

                        VizMessage message = VizMessage.Parser.ParseDelimitedFrom(msgFile);
                        messageReadAttempt++;
                        
                        if (timestepsTotal<2) //Need at least two good messages to display file, after those have been found, stop checking message length (expensive)
                        {
                            if (message.ToString().Length > 3) //Empty message.ToString = "{ }", so length 3
                            {
                                if (firstMsg)
                                {
                                        bufferStartIndex = 0;
                                        CurrentIndex = 0;
                                        firstMessage = message.Clone();
                                        if (firstMessage.Settings != null)
                                        {
                                            SetBufferLimitAndIndexSpacing(firstMessage.Settings.MessageBufferSize,
                                                msgFile.Length,
                                                msgFile.Position, testBufferSize);
                                        }

                                        SettingsMessageReceived = true;
                                        firstMsg = false;
                                }

                                AddMessage(timestepsTotal, message);
                                bufferEndIndex = timestepsTotal;

                                timestepsTotal++;
                            }else                         
                            {
                                Debug.Log($"Message {messageReadAttempt} was empty.");
                            }
                        }else{
                            if (msgFile.Position < bufferLimit)
                            {
                                AddMessage(timestepsTotal, message);
                                bufferEndIndex = timestepsTotal;
                            }

                            timestepsTotal++;
                        }

                    }
                    catch(Exception ex)
                    {
                        VizardGUISettings.UpdateErrorMessages(
                            $"Parsing failed on message {messageReadAttempt} in file with exception:\n {ex.Message}. \nMoving to next message.");
                        messageReadAttempt++;
                    }
                }
            }
        }        
        InBufferLoad = false;
        if (timestepsTotal == 0)
        {
            return false;
        }
        TimeStepSize = messages[1].CurrentTime.SimTimeElapsed -
                       messages[0].CurrentTime.SimTimeElapsed;
        SetVisibleHistoryRanges();

        CurrentIndex = 0;
        return true;
    }

    private static void SetBufferLimitAndIndexSpacing(long bufferLimitSetting, long fileSize, long msgSize, long testBufferSize = 0)
    {
        //Set Buffer Size Limit
        long bufferSizeSetting = bufferLimitSetting;
        if (testBufferSize > 0) //FOR UNIT TEST PURPOSES ONLY
        {
            bufferSizeSetting = testBufferSize;
        }

        if ((bufferSizeSetting < 0) || (bufferSizeSetting > 0.9 * fileSize)) //read in entire file
        {
            bufferLimit = fileSize + 1000;
        }
        else if (bufferSizeSetting > 0)
        {
            bufferLimit = Math.Max(bufferSizeSetting, 10 * msgSize);
        }
        
        //Check that you are not building a List<long> that is larger than C# allows
        while (((fileSize / msgSize) / (countToNextMessagePositionIndex)) >
               1134217728) //max length of List<long> on 64-bit machine
        {
            countToNextMessagePositionIndex *= 10;
            VizardGUISettings.UpdateErrorMessages(
                $"File size is very large, increasing distance between indexed file positions to every {countToNextMessagePositionIndex}th message");
        }
    }

    private static int CalculatePositionIndex(int targetIndex)
    {
        float messagePositionIndex = (float) targetIndex / countToNextMessagePositionIndex;
        int positionIndex = (Mathf.FloorToInt(messagePositionIndex));
        return positionIndex;
    }

    private static void LoadNextBuffer(int targetIndex)
    {
        if (!InBufferLoad)
        {
            InBufferLoad = true;
            int messageFilePositionIndex = CalculatePositionIndex(targetIndex);
            long currentFilePosition = messageFilePositions[messageFilePositionIndex];
            int messageIndexAtCurrentFilePosition = messageFilePositionIndex * countToNextMessagePositionIndex;
            int desiredIndex = targetIndex;
            tempHoldMessage = messages[CurrentIndex].Clone();
            messages.Clear();
            bool firstFwdMsg = true;
            int messageReadAttempt = 0;
            long currentBufferSize = 0;
            using (FileStream msgFile = File.Open(filepath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                msgFile.Position = currentFilePosition;
                while (currentBufferSize < bufferLimit) //Get to desired message's file position
                {
                    try
                    {
                        VizMessage testMessage = VizMessage.Parser.ParseDelimitedFrom(msgFile);

                        if (messageIndexAtCurrentFilePosition >= desiredIndex)
                        {
                            AddMessage(messageIndexAtCurrentFilePosition, testMessage);
                            if (firstFwdMsg)
                            {
                                bufferStartIndex = messageIndexAtCurrentFilePosition;
                                firstFwdMsg = false;
                            }

                            currentBufferSize += msgFile.Position - currentFilePosition;
                            bufferEndIndex = messageIndexAtCurrentFilePosition;
                        }

                        messageIndexAtCurrentFilePosition++;
                        messageReadAttempt++;
                    }
                    catch
                    {
                        VizardGUISettings.UpdateErrorMessages($"Parsing failed on message {messageReadAttempt} in file. Moving to next message.");
                        messageReadAttempt++;
                    }

                    currentFilePosition = msgFile.Position;
                    if (currentFilePosition >= msgFile.Length)
                    {
                        currentFilePosition = 0;
                        messageIndexAtCurrentFilePosition = 0;
                        desiredIndex = 0;
                        msgFile.Position = 0;
                        messageReadAttempt = 0;
                        
                    }
                }

                InBufferLoad = false;
            }
        }
    }

    public static int LoadedMessagesCount
    {
        get { return messages.Count; }
    }

    public static void ClearMessages()
    {
        timestepsTotal = 0;
        messages.Clear();
    }

    public static void SaveMessages(string filename)
    {
        if (DataManager.InNoDisplayMode)
        {
            Debug.Log("In -noDisplay mode, only the twenty most recently received messages are retained in memory. Saving these remaining messages to file.");
        }
        SaveMessageSubset(filename, 0, MessageList.TimestepsTotal - 1); //indexing using frame number
    }

    public static string SaveMessageSubset(string filename, int startingFrame, int endingFrame)
    {
        int startID = startingFrame;
        if (startID < 0)
        {
            startID = 0;
        }

        int endID = endingFrame;
        if (endID >= timestepsTotal)
        {
            endID = timestepsTotal - 1;
        }

        string saveFilepath = $"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}/VizardData/{filename}";
        if (!Directory.Exists(Path.GetDirectoryName(saveFilepath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(saveFilepath));
        }

        using (var output = File.Create(saveFilepath))
        {
            for (int i = startID; i <= endID; i++)
            {
                try
                {
                    Google.Protobuf.MessageExtensions.WriteDelimitedTo(messages[i], output);
                }
                catch
                {
                    if (!DataManager.InNoDisplayMode)
                    {
                        Debug.Log($"Message at index {i} was not found. Going to next index.");
                    }
                }
            }
        }

        return saveFilepath;
    }

    public static void AddSettingsMessageToFirstMessage(VizMessage vizMessage)
    {
        if (firstMessage == null)
        {
            firstMessage = vizMessage;
        }
        else
        {
            firstMessage.Settings = vizMessage.Settings;
        }

        SettingsMessageReceived = true;
    }


    public static double[,] GetPositionHistoryBSK(bool isSpacecraft, int objectIndex)
    {
        int startIndex = startMsgIndexToPlot; //In live streaming, the static value can change in the middle of this function
        int endIndex = endMsgIndexToPlot; //In live streaming, the static value can change in the middle of this function
        double[,] positions = new double[endIndex - startIndex + 1, 3];
        if (objectIndex != -1)
        {
            int j = 0;
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (isSpacecraft)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        positions[j, k] = messages[i].Spacecraft[objectIndex].Position[k];
                    }
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        positions[j, k] = messages[i].CelestialBodies[objectIndex].Position[k];
                    }
                }
                j++;
            }
        }

        return positions;
    }

    public static double[,] GetVelocityHistoryBSK(bool isSpacecraft, int objectIndex)
    {
        int startIndex = startMsgIndexToPlot; //In live streaming, the static value can change in the middle of this function
        int endIndex = endMsgIndexToPlot; //In live streaming, the static value can change in the middle of this function
        double[,] velocities = new double[endIndex - startIndex + 1, 3];

        if (objectIndex != -1)
        {
            int j = 0;
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (isSpacecraft)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        velocities[j, k] = messages[i].Spacecraft[objectIndex].Velocity[k];
                    }
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        velocities[j, k] = messages[i].CelestialBodies[objectIndex].Velocity[k];
                    }
                }
                j++;
            }
        }
        return velocities;
    }
    
    public static double[,] GetRotationHistoryDCM_BSK(bool isSpacecraft, int objectIndex)
    {
        int startIndex = startMsgIndexToPlot; //In live streaming, the static value can change in the middle of this function
        int endIndex = endMsgIndexToPlot; //In live streaming, the static value can change in the middle of this function
        double[,] rotations = new double[endIndex - startIndex + 1, 9];

        if (objectIndex != -1)
        {
            int j = 0;
            double[] thisRot;
            for (int i = startIndex; i <= endIndex; i++)
            {
                thisRot = GetRotationDCM_BSK(isSpacecraft, objectIndex, i);
                for (int k = 0; k < 9; k++)
                {
                    rotations[j, k] = thisRot[k];
                }

                j++;
            }
        }
        return rotations;
    }

    public static double[] GetRotationDCM_BSK(bool isSpacecraft, int objectIndex, int desiredIndex)
    {
        if (isSpacecraft) //Spacecraft rotation has 3 terms
        {
            double[] MRP = 
            {
                messages[desiredIndex].Spacecraft[objectIndex].Rotation[0],
                messages[desiredIndex].Spacecraft[objectIndex].Rotation[1],
                messages[desiredIndex].Spacecraft[objectIndex].Rotation[2]
            };
            return OrbitVectorMath.ConvertRightHandedMRPToRightHandedDCM(MRP);
        }
        //return celestial body rotation
        return new []
        {
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[0],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[1],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[2],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[3],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[4],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[5],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[6],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[7],
            messages[desiredIndex].CelestialBodies[objectIndex].Rotation[8]
        };
    }

    public static void GetTruePathColorHistory(TruePathLinePlotter truePathLinePlotter, int scIndex, bool isOrbitLine)
    {
        if (scIndex != -1)
        {
            truePathLinePlotter.lineColors = new List<Color>();
            truePathLinePlotter.colorChangeIndices = new List<int>();

            Color currentColor = truePathLinePlotter.defaultTruePathColor;
            if (truePathLinePlotter.lineColors.Count > 0)
            {
                currentColor = truePathLinePlotter.lineColors.Last();
            }

            for (int i = startMsgIndexToPlot; i <= endMsgIndexToPlot; i++)
            {
                Color newColor = isOrbitLine?
                    SpacecraftStateUtilities.GetTruePathColorFromMessage(messages[i].Spacecraft[scIndex],
                        currentColor):SpacecraftStateUtilities.GetGroundTrackColorFromMessage(messages[i].Spacecraft[scIndex],
                        currentColor);

                if ((newColor != currentColor)||(i==startMsgIndexToPlot))
                {
                    truePathLinePlotter.lineColors.Add(newColor);
                    truePathLinePlotter.colorChangeIndices.Add(i);
                    currentColor = newColor;
                }
            }

            if (truePathLinePlotter.lineColors.Count == 0) //Should not be needed anymore
            {
                truePathLinePlotter.lineColors.Add(truePathLinePlotter.defaultTruePathColor);
            }
        }
    }

    public static int NumberOfMessagesInBuffer()
    {
        return messages.Count;
    }

    public static long GetBufferLimit()
    {
        return bufferLimit;
    }

    public static int GetIndexSpacing()
    {
        return countToNextMessagePositionIndex;
    }

    public static VizMessage GetMessageAtIndex(int desiredIndex)
    {
        if (messages.ContainsKey(desiredIndex))
        {
            return messages[desiredIndex];
        }
        else
        {
            return null;
        }
    }

    public static void ResetFirstMessage()
    {
        SettingsMessageReceived = false;
        firstMessage = null;
        firstMessageAdded = false;
    }

    public static void WriteOutCSV(string filename, int startingIndex, int endingBeforeIndex)
    {
        string saveFilePath = $"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}/VizardData/{filename}";
        if (!Directory.Exists(Path.GetDirectoryName(saveFilePath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(saveFilePath));
        }

        using (var writer = new StreamWriter(saveFilePath, false))
        {
            var content = new StringBuilder();
            for (int i = startingIndex; i < endingBeforeIndex; i++)
            {
                VizMessage.Types.Spacecraft myMsg = messages[i].Spacecraft[1];
                string lineToWrite = $"{myMsg.Rotation[0]}, {myMsg.Rotation[1]}, {myMsg.Rotation[2]}\n";
                content.Append(lineToWrite);
            }
            writer.Write(content);
        }
        Debug.Log($"Wrote CSV file to {saveFilePath}");
    }

    public static void CompressLiveMessages(int numerator, int denominator)
    {
        if (DataManager.IsLiveSim)
        {
            Debug.LogFormat("Compression of {0} messages by {1}/{2} begins.", messages.Count, numerator, denominator);
            int saveCtr = 0;
            int newIndex = 0;
            for (int i=0; i < timestepsTotal; i++){
			
                VizMessage msg;
                //Remove the message
                messages.TryRemove(i, out msg );
                //Reset CurrentIndex to stay in the same part of playback after compression
                if (CurrentIndex == i){
                    CurrentIndex = newIndex;
                }
                //Now decide if it needs to be reassigned to a new index or discarded completely
                if (saveCtr<numerator){
                    messages.TryAdd(newIndex,msg);
                    saveCtr++;
                    newIndex++;
                }else{
                    //Remove message completely
                    saveCtr++;
                }
                if (saveCtr == denominator){
                    saveCtr = 0;
                }
            }
            Debug.LogFormat("There were {0} messages and now there are {1} messages", timestepsTotal, messages.Count);
            timestepsTotal = newIndex;
            SetVisibleHistoryRanges();
            SetNextIndex(timestepsTotal);
        }
    }

    private static void ResetMessageListVariables()
    {
        bufferLimit = 10000000;
        countToNextMessagePositionIndex = 10;
        bufferStartIndex = -1;
        bufferEndIndex = -1;

        startMsgIndexToPlot=0;
        endMsgIndexToPlot =0;
        VisibleHistoryUpdateCount = 0;
        firstMessageAdded=false;
        DesiredNextIndex = -1;
        timestepsTotal=0;
    }
}
