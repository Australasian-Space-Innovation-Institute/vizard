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

using UnityEngine.UI;
using UnityEngine;
/// <summary>
/// Handles user input to the TimeSlider in the PlaybackControlsBar
/// </summary>
public class TimeSlider : MonoBehaviour {
	private Slider slider;
	UISelectHandler handleSelect;
	private bool scrubbing; //Handle held last frame: log the start and end of a scrub, not every frame of it

	[Header("Required Reference to GameController")]
	public ItsAboutTime timeController;

	void Start(){
		slider = GetComponent<Slider> ();
		handleSelect = slider.GetComponentInChildren<UISelectHandler> ();
	}
		
	// Update is called once per frame
	void Update () {
		if (handleSelect.IsSelected) {
				if (!scrubbing) {
					scrubbing = true;
					//Look at the past while the live run keeps streaming into the buffer
					MessageList.FollowLive = false;
					VizardCommandLog.Log ("scrub_start");
				}
				//Also stop the jump to the newest frame that each arriving frame (or
				//Vizard's own pause, which keeps it set) would otherwise make
				DataManager.DisplayMostRecentMessage = false;
				float timeValue = slider.value;
				if (LiveSmoothing.Active) {
					//A live run: hold the view at this frame; the stream keeps arriving
					int index = Mathf.Min (Mathf.FloorToInt (timeValue * MessageList.TimestepsTotal),
					                       MessageList.TimestepsTotal - 1);
					LiveSmoothing.ScrubTo (index);
				} else {
					timeController.SetArchiveFraction (timeValue);
				}
		} else {
				if (scrubbing) {
					scrubbing = false;
					if (LiveSmoothing.Active) {
						//Released: replay from here at the simulation's speed, or live at the end
						LiveSmoothing.EndScrub (slider.value >= 0.999f);
					} else if (slider.value >= 0.999f) {
						//Dragged to the end: back to following the live stream
						MessageList.FollowLive = true;
						DataManager.DisplayMostRecentMessage = true;
					}
					VizardCommandLog.Log ("scrub_end", "follow_live=" + (MessageList.FollowLive ? "true" : "false"));
				}
				slider.value = timeController.GetArchiveFraction ();
		}
	}
}
