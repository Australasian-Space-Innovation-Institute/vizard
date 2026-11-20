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
using TMPro;
using UnityEngine;
using VizProtobufferMessage;
/// <summary>
/// Initializes and updates a single Location per its specific VizMessage
/// <remarks>If Full Location features are turned on, the
/// FullLocationAddOn prefab will be added with its FullLocationMethods.cs
/// script to this Location GameObject</remarks>
/// </summary>
public class DrawLocationMarker : MonoBehaviour
{
    private GameObject myLabel;
    private GameObject myMarker;
    private GameObject myStationButton;

    public bool isFullLocation;
    private FullLocationMethods fullLocationMethods;

    public bool updateLocationFromMessages;

    [HideInInspector]public GameObject parentBody;
    private bool parentBodyIsSpacecraft;
    private double parentBodyRadius;
    private float desiredMarkerScale = 1f;
    [HideInInspector]public double[] locationOriginBSKCS = {0, 0, 0};
    private Vector3 stationNormal;
    private readonly float bodyOffsetFactor = 1.0001f;

    private Dictionary<int, VizMessage.Types.Location> myMsgs = new Dictionary<int, VizMessage.Types.Location>();
    private int firstIndex=-1;
    private int currentAppliedMsgIndex=-1;
    
    private Camera cameraToUse;

    // SCARLET demo: a location given its own label (Location.label, not empty and
    // not "NOLABEL") always shows it, whatever the global location-label setting
    // (Basilisk's showLocationLabels, the Labels panel); it still hides behind the
    // planet like any label. Locations without their own label follow the global
    // setting as before.
    private bool hasOwnLabel;
    private bool LabelShown => VizardGUISettings.ShowLocationLabels || hasOwnLabel;
    private readonly int layerMask = ((1 << 7) | (1 << 9) | (1 << 11) | (1 << 14)); //7=UnlitSC, 9=TrueBodySizeColliders, 11 = Spacecraft, 14 = Antenna (using this instead of LabelMarkers to get double work out of collider)
    
    

    public bool InitializeLocationMarker(VizMessage.Types.Location myMsg, int msgIndex, bool enableFullLocation)
    {
        myMsgs[msgIndex] = myMsg.Clone();
        firstIndex = msgIndex;
        currentAppliedMsgIndex = msgIndex;
        isFullLocation = enableFullLocation;
        
        myMarker = transform.GetChild(0).gameObject;
        cameraToUse = MainCameraUtilities.MainCamera;
        this.name = myMsg.StationName;
        myMarker.name = name + "MarkerSphere";
        if (myMsg.MarkerScale <= 0)
        {
            myMsgs[msgIndex].MarkerScale = 1;
            desiredMarkerScale = 1;
        }
        else
        {
            desiredMarkerScale = (float) myMsg.MarkerScale;
        }
        bool parentFound = SetParentBody(myMsg.ParentBodyName);
        if (parentFound)
        {
            myLabel = LabelMaker.CreateLabel(name, "Label", myMarker, new Vector2(10, 0), "Locations", 0);
            myLabel.GetComponent<TextMeshProUGUI>().text = name; //Set label to display station name by default
            SetLabelText(myMsg);
            myLabel.SetActive(LabelShown); // SCARLET demo: was ShowLocationLabels, set before the label was read
            if (isFullLocation)
            {
                myMarker.GetComponent<Renderer>().material = Instantiate(Resources.Load("Materials/Spacecraft_HUD/TransparentMat", typeof(Material)) as Material);
            }
            SetColor(myMsg);
            SetLocOriginForParentBody(myMsg);
            SetLocNormal(myMsg);
            UpdateMarkerAppearance();
            if (isFullLocation)
            {
                GameObject fullLocation = Instantiate (Resources.Load("Prefabs/FullLocationAddOn")as GameObject, transform);
                fullLocationMethods = fullLocation.GetComponent<FullLocationMethods>();
                fullLocationMethods.InitializeFullLocation(myMsg, (float) parentBodyRadius, parentBodyIsSpacecraft,myMarker.GetComponent<Renderer>().material.color);
            }
            return true;
        }

        return false;
    }

    void FixedUpdate()
    {
        int currentIndex = MessageList.CurrentIndex;
        if (currentIndex < firstIndex)
        {
            HideMarker(true);

        }else{
            int correctIndex = firstIndex;
            foreach (int msgIndex in myMsgs.Keys)
            {
                if (msgIndex <= currentIndex)
                {
                    correctIndex = msgIndex;
                }
                else
                {
                    break;
                }
            }
            if (correctIndex != currentAppliedMsgIndex)
            {
                ApplyLocationSettings(myMsgs[correctIndex]);
                currentAppliedMsgIndex = correctIndex;
            }

            UpdateMarkerAppearance();
            // SCARLET demo: an own label comes back after the Labels panel or HideMarker turned it off
            if (hasOwnLabel && !myLabel.activeSelf)
            {
                myLabel.SetActive(true);
            }
            if (LabelShown)
            {
                myLabel.GetComponent<TextMeshProUGUI>().enabled = CheckLocationMarkerVisibleInCamera();
            }
        }
    }

    private void HideMarker(bool isHidden)
    {
        myMarker.SetActive(!isHidden);
        if (myLabel != null)
        {
            // SCARLET demo: an own label is off while its location is hidden
            myLabel.SetActive(hasOwnLabel ? !isHidden : isHidden&&VizardGUISettings.ShowLocationLabels);
        }
    }
    
    public void ProcessLocationMessage(VizMessage.Types.Location locMsg, int msgIndex)
    {
        //Note that only messages that have never been processed before will be forwarded here by LocationManager
        //First check that the new msg is different from the last received old message (because we were sending
        //lots of duplicate messages the old way
        VizMessage.Types.Location currentLocConfig = myMsgs[currentAppliedMsgIndex];
        if (!locMsg.Equals(currentLocConfig))
        {
            myMsgs[msgIndex] = locMsg.Clone();
            currentAppliedMsgIndex = msgIndex;
            ApplyLocationSettings(locMsg);  
        }
    }
    
    public void ApplyLocationSettings(VizMessage.Types.Location locMsg, bool fromPanel=false){
        if (locMsg.IsHidden)
        {
            myMarker.SetActive(false);
            myLabel.GetComponent<TextMeshProUGUI>().enabled = false;
            if (isFullLocation)
            {
                fullLocationMethods.gameObject.SetActive(false);
            }

        }
        else
        {
            myMarker.SetActive(true);

            myLabel.GetComponent<TextMeshProUGUI>().enabled = true;
            if (fromPanel)
            {
                myMsgs[0] = locMsg.Clone();
            }
            UpdateLocationFromMessages(locMsg);
            if (isFullLocation)
            {
                fullLocationMethods.gameObject.SetActive(true);
                fullLocationMethods.UpdateFullLocationFromMessages(locMsg, (float) parentBodyRadius);
            }

            UpdateMarkerAppearance();
        }
    }

    private bool SetParentBody(string parentName)
    {
        parentBody = CelestialBodyStateUtilities.GetGameObjectWithBodyName(parentName);
        if (parentBody != null)
        {
            gameObject.transform.SetParent(parentBody.transform);
            transform.localScale = Vector3.one;

            if (parentBody.CompareTag("Planet"))
            {
                parentBodyRadius = parentBody.GetComponent<PlanetController>().planetRadius;
                parentBodyIsSpacecraft = false;
            }
            else if (parentBody.CompareTag("Spacecraft"))
            {
                parentBodyRadius = 1;
                parentBodyIsSpacecraft = true;
            }
            else if (parentBody.CompareTag("Sun"))
            {
                parentBodyRadius = parentBody.GetComponent<SunBuilder>().sunRadius;
                parentBodyIsSpacecraft = false;
            }

            return true;
        }

        return false;
    }

    private void SetLabelText(VizMessage.Types.Location myMsg)
    {
        if (!String.IsNullOrEmpty(myMsg.Label))
        {
            myLabel.GetComponent<TextMeshProUGUI>().text = (myMsg.Label == "NOLABEL") ? "" : myMsg.Label;
            hasOwnLabel = myMsg.Label != "NOLABEL"; // SCARLET demo: empty leaves the label as it was
        }
    }
    
    public void SetColor(Color newColor)
    {
        myMarker.GetComponent<MeshRenderer>().material.color = newColor;
        if (isFullLocation)
        {
            fullLocationMethods.SetColor(newColor);
        }
    }

    private void SetColor(VizMessage.Types.Location myMsg)
    {
        if (myMsg.Color.Count >= 3)
        {
            Color newColor = new Color(myMsg.Color[0] / 255f, myMsg.Color[1] / 255f,
                myMsg.Color[2] / 255f, 1f);
            if (isFullLocation)
            {
                if (myMsg.Color.Count > 3)
                {
                    newColor.a = myMsg.Color[3] / 255f;
                }

                if (fullLocationMethods != null)
                {
                    fullLocationMethods.SetColor(newColor);
                }
            }
            // LocationMarkers use the Unlit/Opaque Shader to be as lightweight as possible
            // if (myMsg.Color.Count >= 4)
            // {
            //     newColor.a = (float) myMsg.Color[3] / 255f;
            // }
            myMarker.GetComponent<MeshRenderer>().material.color = newColor;

        }
    }

    private void SetLocOriginForParentBody(VizMessage.Types.Location myMsg)
    {
        locationOriginBSKCS = new[] {myMsg.RGPP[0], myMsg.RGPP[1], myMsg.RGPP[2]};
        if (parentBody.CompareTag("Planet") || parentBody.CompareTag("Sun"))
        {
            Vector3 scaledOrigin = new Vector3((float) (locationOriginBSKCS[1] / parentBodyRadius),
                (float) (locationOriginBSKCS[2] / parentBodyRadius),
                (float) -(locationOriginBSKCS[0] / parentBodyRadius));
            transform.localPosition = bodyOffsetFactor * scaledOrigin;
        }
        else if (parentBody.CompareTag("Spacecraft"))
        {
            transform.localPosition = new Vector3((float) locationOriginBSKCS[1], (float) locationOriginBSKCS[2],
                (float) -locationOriginBSKCS[0]);
        }
    }
    
    private void SetLocNormal(VizMessage.Types.Location myMsg)
    {
        stationNormal = (new Vector3((float) myMsg.GHatP[0], (float) myMsg.GHatP[1], (float) myMsg.GHatP[2]));
        Quaternion directionToPoint =
            Quaternion.LookRotation(new Vector3(stationNormal.y, stationNormal.z,
                -stationNormal.x)); //Converted to Unity CS
        transform.localRotation = directionToPoint;
    }

    private void UpdateMarkerAppearance()
    {
        Vector3 capsuleShape = new Vector3(1f, 1f, 1.2f) * desiredMarkerScale;
        float markerScale = VizardGUISettings.CalculateScaleForLocationMarker(transform);
        myMarker.transform.localScale = capsuleShape * (markerScale / parentBody.transform.localScale.x);
        if (LabelShown) // SCARLET demo: was ShowLocationLabels
        {
            myLabel.GetComponent<TextMeshProUGUI>().enabled = CheckLocationMarkerVisibleInCamera();
        }
    }

    ///<remarks>This is called by a BroadcastMessage from parent body
    /// when the parent body goes into sprite mode. DO NOT DELETE, IT
    /// IS IN USE.
    ///</remarks>
    private void ConfigureHUDForSpriteMode(bool inSpriteMode) //Called by broadcast message
    {
        myMarker.SetActive(!inSpriteMode);
        if (isFullLocation)
        {
            fullLocationMethods.gameObject.SetActive(!inSpriteMode);
        }
    }

    private bool CheckLocationMarkerVisibleInCamera()
    {
        if (DataManager.FirstMessageDisplayed)
        {
            Vector3 origin = cameraToUse.transform.position;
            Vector3 direction = transform.position - origin;
            float maxDistance = direction.magnitude * 1.2f;

            if (Physics.Raycast(origin, direction, out var hit, maxDistance, layerMask))
            {
                if (myMarker == hit.collider.gameObject)
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private void UpdateLocationFromMessages(VizMessage.Types.Location locMsg)
    {
        SetLabelText(locMsg);
        SetColor(locMsg);
        string parentName = locMsg.ParentBodyName;
        if ((parentName != "") && (parentName != parentBody.name))
        {
            SetParentBody(parentName);
        }
        SetLocOriginForParentBody(locMsg);
        SetLocNormal(locMsg);
        desiredMarkerScale = (float) (locMsg.MarkerScale > 0 ? locMsg.MarkerScale : desiredMarkerScale);
    }

    void OnDestroy()
    {
        CelestialBodyStateUtilities.LocationsDictionary.Remove(this.name);
        Destroy(myLabel);
    }

    public VizMessage.Types.Location GetCurrentLocationSettings()
    {
        return myMsgs[currentAppliedMsgIndex];
    }

    public Color GetLocationColor()
    {
        return myMarker.GetComponent<Renderer>().material.color;
    }
    
        
    public void SetInventoryButton(GameObject button)
    {
        myStationButton = button;
    }

    public GameObject GetInventoryButton()
    {
        return myStationButton;
    }

    public void UpdateLineRendererSettings(bool isOn)
    {
        fullLocationMethods.UpdateLineRendererSettings(isOn);
    }

    public void CleanAntennaInViewList()
    {
        if (isFullLocation)
        {
            fullLocationMethods.CleanAntennaInViewList();
        }
    }
}