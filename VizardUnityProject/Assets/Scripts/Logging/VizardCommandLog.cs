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
using System.Globalization;
using UnityEngine;

/// <summary>
/// Records what the user did in Vizard's GUI, for debugging alongside the
/// application that is streaming to it.
/// <remarks>Each command is one line in the Unity log (the -logFile):
/// <code>VIZARD_CMD|yyyy-MM-dd HH:mm:ss.fff|command|key=value key=value</code>
/// The Scarlet Alpha demo app reads these lines from that file into its own
/// command log. Each line carries where playback was at the time.</remarks>
/// </summary>
public static class VizardCommandLog
{
    public const string Marker = "VIZARD_CMD|";

    /// <param name="command">What was done, e.g. "pause"</param>
    /// <param name="details">key=value fields, space separated; see Value()</param>
    public static void Log(string command, string details = "")
    {
        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string line = $"{Marker}{time}|{command}|{details} {PlaybackContext()}".Replace("| ", "|");
        //No stack trace: one line per command keeps the log readable
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", line);
    }

    /// <summary>
    /// A value that is safe in a key=value field: no spaces or separators.
    /// </summary>
    public static string Value(object value)
    {
        return value == null ? "none" : value.ToString().Replace(' ', '_').Replace('|', '/');
    }

    /// <summary>
    /// The message on screen and its sim time, e.g. "msg=40/215 sim_t=800".
    /// </summary>
    private static string PlaybackContext()
    {
        if (MessageList.TimestepsTotal <= 0)
        {
            return "msg=none";
        }

        string position = $"msg={MessageList.CurrentIndex + 1}/{MessageList.TimestepsTotal}";
        try
        {
            double simSeconds = MessageList.CurrentMessage.CurrentTime.SimTimeElapsed / 1e9;
            return position + " sim_t=" + simSeconds.ToString("F0", CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return position; //No message to read the time from (e.g. mid-reset)
        }
    }
}
