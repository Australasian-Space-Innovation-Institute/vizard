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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;
using VizProtobufferMessage;

/// <summary>
/// Initializes and updates the state of its assigned
/// spacecraft object from the current scenario
/// <remarks>Builds all attached instruments and actuators,
/// turns on/off sprite mode, labels,and HUD elements
/// </remarks>
/// </summary>
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class SpacecraftController : MonoBehaviour {
    public int spacecraftIndex;
    public string spacecraftName;
    public string parentSpacecraftName;
    public bool isEffector;
    private int parentSpacecraftIndex=-1;
    private GameObject parentSpacecraft;
	
    public int spacecraftParentBodyIndex;
    public int spacecraftNearbySmallBodyIndex;
    public GameObject orbitLine;
	
    public GameObject spacecraftModel;
    public GameObject clickableCollider;
    public GameObject inertialCoordinateAxes;
    public GameObject hillFrameCoordinateAxes;
    public GameObject velocityFrameCoordinateAxes;
    public GameObject spacecraftSprite;
    public GameObject antennaCollider;
    public List<Vector3> hillFrameAxes = new List<Vector3>();

    private GameObject nameLabel;
    private List<GameObject> allMyLabels = new List<GameObject>();
    private Dictionary<string, GameObject> HUDcontainers = new Dictionary<string, GameObject>();

    private bool isVisible=true;
    private Camera cameraToUse;
#if !VIZARD_OPENXR
    private readonly int layerMask = ((1 << 7)| (1 << 9)|(1 << 11)); //7 = Unlit Spacecraft 9 = True Body Size Colliders, 11 = Spacecraft 
#endif

    private double ratioProjectionToTrueDistanceFromCam=1f;
	
    public float meshDimension;
    public float minDimension;

    private Vector3 meshCenter;
    private VizMessage.Types.ActuatorSettings myGUIActuatorSettings;
    private VizMessage.Types.InstrumentSettings myGUIInstrumentSettings;
    private bool spriteWasOnLastFrame;
    private bool usingDefaultSprite = true;

    private string modelKey;
    private bool useLocalModel = true;
    private VizProtobufferMessage.VizMessage.Types.CustomModel myModelSettings;

    private AsyncOperationHandle<GameObject> modelHandle;

    private bool usingHillFrame;

    private bool inNoDisplayMode;

    private bool needRemoteModel;
    private bool inLoad;
    private bool askForCameraUpdate;
	

    public string InitializeSpacecraft(int index, bool inTestMode = false)
    {
        if (DataManager.UseVR)
        {
            usingHillFrame = true;
        }
        spacecraftIndex = index;
        VizMessage.Types.Spacecraft myMsg = MessageList.FirstMessage.Spacecraft[index];
        parentSpacecraftName = myMsg.ParentSpacecraftName;
		
        if (!String.IsNullOrEmpty(parentSpacecraftName))
        {
            spacecraftSprite.GetComponent<SpriteRenderer> ().color = new Color(0,0,0,0 ); //Make the sprite transparent because effectors should not show sprites
            parentSpacecraftIndex = SpacecraftStateUtilities.GetSpacecraftIndex(parentSpacecraftName);
            parentSpacecraft = SpacecraftStateUtilities.GetSpacecraftObject(parentSpacecraftIndex);
            isEffector = true;
        }
        else
        {
            gameObject.tag = "Spacecraft";
        }

        inNoDisplayMode = DataManager.InNoDisplayMode;
        spacecraftName = this.name; 
        modelKey = myMsg.ModelDictionaryKey;

        if (modelKey == "")
        {
            modelKey = "bskSat";
        }

        if (spacecraftName == "inertial")
        {
            modelKey = "EMM";
        }

        if (spacecraftName == "MAX")
        {
            modelKey = "MAX";
        }
        if (modelKey!="bskSat")
        {
            ApplyModelKeySetting(modelKey);
        }
        myModelSettings = new VizProtobufferMessage.VizMessage.Types.CustomModel
        {
            ModelPath = modelKey,
            SimBodiesToModify = {spacecraftName},
            Offset = {0, 0, 0},
            Rotation = {0, 0, 0},
            Scale = {1, 1, 1},
        };
		
        int[] parentBodies = OrbitVectorMath.FindPrimaryBody(spacecraftIndex, true);
        spacecraftParentBodyIndex = parentBodies[0];
        spacecraftNearbySmallBodyIndex = parentBodies[1];

        cameraToUse = Camera.main;

        // Name the collider that allows the user to double-click and select planet
        clickableCollider.name = spacecraftName + "ClickableCollider";

        // Name the spacecraftMesh 
        spacecraftModel.name = spacecraftName + "SpacecraftModel";
		
        myGUIActuatorSettings = VizardGUISettings.GetActuatorSettings(spacecraftName);
        myGUIInstrumentSettings = VizardGUISettings.GetInstrumentSettings(spacecraftName);

        CalculateMeshDimension();
        if (!inTestMode)
        {
            AddActuators();
            AddInstruments();
            AddLights();
            AddEllipsoids();
            AddMultiShapes();

            CreateLabels();

            hillFrameCoordinateAxes.GetComponent<DrawAxes>().localFrame = false;
            velocityFrameCoordinateAxes.GetComponent<DrawAxes>().localFrame = false;

            string texturePath = myMsg.LogoTexture;
            Texture2D myLogo = null;
            if (!String.IsNullOrEmpty(texturePath))
            {
                if ((!DataManager.IsLiveSim)&&(texturePath.StartsWith(".")))
                {
                    texturePath = Path.GetFullPath(texturePath, Path.GetDirectoryName(DataManager.FilePath));
                }
                myLogo = LoadAndApplyLogoTexture(texturePath);
            }

            SetSpacecraftSpecificSprite(myMsg.SpacecraftSprite, myLogo);
            ApplyCurrentEmissionSetting();
            clickableCollider.SetActive(true);
            MainCameraUtilities.FindAllReflectionProbes();
        }

        return parentSpacecraftName;
    }

    // Update is called once per frame
    void FixedUpdate () {

            if ((needRemoteModel) && (GoodEnoughAddressables.AllRemoteCatalogsLoaded) && (!inLoad))
            {
                ApplyModelKeySetting(modelKey);
            }

            if (askForCameraUpdate)
            {
                MainCameraUtilities.MainCamera.GetComponent<MainCameraViewManager>()
                    .SetupChangeOfMainCameraTarget(MainCameraUtilities.CameraTarget);
                askForCameraUpdate = false;
            }

            if (!inNoDisplayMode)
            {
                if (isEffector)
                {
                    UpdateEffector();
                }
                else
                {
                    UpdateSpacecraft();
                }
            }
        
    }

    public void UpdateSpacecraft(){
        double[] myPosition = SpacecraftStateUtilities.GetAbsSpacecraftPositionUnityCS (spacecraftIndex);
        bool scBeyondThresholdForSprites = false;
        ratioProjectionToTrueDistanceFromCam = 1f;
		
        if (CelestialBodyStateUtilities.ViewIsLocal) {
            if (MainCameraUtilities.CameraTarget == this.gameObject)
            {
                myPosition = new double[] {0, 0, 0};
                if (CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
                {
                    double[] camPositionMeters = MainCameraUtilities.GetAbsoluteMainCameraPositionInMeters();
                    double[] truePositionFromCameraMeters = OrbitVectorMath.Subtract(myPosition, camPositionMeters);
                    double trueDistanceFromCamMeters = OrbitVectorMath.Magnitude(truePositionFromCameraMeters);
                    double distanceFromCameraUnityUnits = MainCameraUtilities.MainCamera.transform.position.magnitude/CelestialBodyStateUtilities.SpacecraftLocalViewScale;

                    ratioProjectionToTrueDistanceFromCam = distanceFromCameraUnityUnits / trueDistanceFromCamMeters;
                    if (trueDistanceFromCamMeters > 1000f*CelestialBodyStateUtilities.SpacecraftLocalViewScale)
                    {
                        scBeyondThresholdForSprites = true;
                    }
                }
            }
            else
            {
                double[] cameraTargetPosition = MainCameraUtilities.GetCameraTargetAbsolutePositionUnityCS(); //This returns the BSK position rotated into Unity CS

                //Calculate relative position:
                myPosition = OrbitVectorMath.Subtract(myPosition, cameraTargetPosition); //meters
                if (CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
                {
                    double[] camPositionMeters = MainCameraUtilities.GetAbsoluteMainCameraPositionInMeters();
                    double[] truePositionFromCameraMeters = OrbitVectorMath.Subtract(myPosition, camPositionMeters);
                    double trueDistanceFromCamMeters = OrbitVectorMath.Magnitude(truePositionFromCameraMeters);
                    double distanceToProjectionWallMeters =
                        MainCameraUtilities.DistanceToProjectionWallUnityUnits /
                        CelestialBodyStateUtilities.SpacecraftLocalViewScale;
                    double projectionWallStackingConstantMeters = MainCameraUtilities.ProjectionWallStackingConstantUnityUnits /
                                                                  CelestialBodyStateUtilities.SpacecraftLocalViewScale;

                    if (trueDistanceFromCamMeters > distanceToProjectionWallMeters)
                    {
                        if (trueDistanceFromCamMeters > MainCameraUtilities.TrueCameraDistanceToTargetMeters)
                        {
                            ratioProjectionToTrueDistanceFromCam =
                                (distanceToProjectionWallMeters + projectionWallStackingConstantMeters * Math.Log10(trueDistanceFromCamMeters-distanceToProjectionWallMeters)) / trueDistanceFromCamMeters;
                        }
                        else
                        {
                            ratioProjectionToTrueDistanceFromCam = distanceToProjectionWallMeters/ trueDistanceFromCamMeters;
                        }
                        myPosition = OrbitVectorMath.ScaleVector(truePositionFromCameraMeters,
                            ratioProjectionToTrueDistanceFromCam); //meters
                        myPosition = OrbitVectorMath.ScaleVector(myPosition,
                            CelestialBodyStateUtilities.SpacecraftLocalViewScale); //Unity Units
                        myPosition = OrbitVectorMath.Add(OrbitVectorMath.ReturnDoubleArray(MainCameraUtilities.MainCamera.transform.position),myPosition); //Unity units
                    }
                    else
                    {
                        myPosition = OrbitVectorMath.ScaleVector(myPosition, CelestialBodyStateUtilities.SpacecraftLocalViewScale); //Unity units
                    }
					
                    if (trueDistanceFromCamMeters > 1000f*CelestialBodyStateUtilities.SpacecraftLocalViewScale)
                    {
                        scBeyondThresholdForSprites = true;
                    }
                }
                else
                {
                    myPosition = OrbitVectorMath.ScaleVector(myPosition,
                        1 / CelestialBodyStateUtilities.LocalPlanetViewScale);
                    scBeyondThresholdForSprites = true;
                }
            }
            CheckForEclipsed();
        } else {
            myPosition = OrbitVectorMath.ScaleVector (myPosition, 1 / CelestialBodyStateUtilities.HelioCenteredViewScale);
            scBeyondThresholdForSprites = true;
        }

        float scaleToUse = GetDesiredSpacecraftScale();

        if (VizardGUISettings.SomeSpacecraftLabelsAreOn){
            int scVisible = CheckForVisibleInCamera();
            if ((scVisible==1)|(scVisible==2)){
                if ((scVisible==1)&&(!isVisible)){
                    isVisible = true;
                }else if((scVisible ==2)&&(isVisible)){
                    isVisible = false;
                }
                UpdateLabelVisibility();
            }
        }
        else
        {
            isVisible = false;
        }

        UpdateHillFrame();
        UpdateVelocityFrame();


        //Update the position of the spacecraft
        Vector3 proposedPosition = OrbitVectorMath.ReturnVector3(myPosition);
        transform.position = proposedPosition.magnitude < Double.PositiveInfinity ? proposedPosition : Vector3.zero;
		
        //Update spacecraft orientation
        transform.rotation = SpacecraftStateUtilities.GetSpacecraftOrientationUnityCS(spacecraftIndex);

        //Update spacecraft's primary body
        if (!SpacecraftStateUtilities.SpacecraftMsgOnly) {
            int[] bodiesToWorryAbout = OrbitVectorMath.FindPrimaryBody (spacecraftIndex,true);
            spacecraftParentBodyIndex = bodiesToWorryAbout[0];
            spacecraftNearbySmallBodyIndex = bodiesToWorryAbout[1];
        }
		
        UpdateSprite(scBeyondThresholdForSprites, scaleToUse);
			
        SetScale (scaleToUse);
    }

    private void UpdateEffector()
    {
        double[] myPosition = SpacecraftStateUtilities.GetAbsSpacecraftPositionUnityCS (spacecraftIndex);
        double[] parentPosition = SpacecraftStateUtilities.GetAbsSpacecraftPositionUnityCS(parentSpacecraftIndex);
        double[] positionRelativeToParentSC = OrbitVectorMath.Subtract(myPosition, parentPosition);
        ratioProjectionToTrueDistanceFromCam = 1f;
		
        Vector3 myOffset = OrbitVectorMath.ReturnVector3(positionRelativeToParentSC);
        if (CelestialBodyStateUtilities.ViewIsLocal)
        {
            if (CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
            {
                ratioProjectionToTrueDistanceFromCam = parentSpacecraft.GetComponent<SpacecraftController>()
                    .ratioProjectionToTrueDistanceFromCam;
                myOffset *= (float) ratioProjectionToTrueDistanceFromCam *
                            (float) CelestialBodyStateUtilities.SpacecraftLocalViewScale;
            }
            else
            {
                myOffset *= SpacecraftStateUtilities.DefaultLocalViewSpacecraftScale;
            }
        }
        else
        {
            myOffset *= SpacecraftStateUtilities.DefaultHelioViewSpacecraftScale;
        }
        //Double check you need all this
		
        transform.position = parentSpacecraft.transform.position + myOffset;
        float scaleToUse = GetDesiredSpacecraftScale();

        if (VizardGUISettings.SomeSpacecraftLabelsAreOn){
            int scVisible = CheckForVisibleInCamera();
            if ((scVisible==1)|(scVisible==2)){
                if ((scVisible==1)&&(!isVisible)){
                    isVisible = true;
                    UpdateLabelVisibility();
                }else if((scVisible ==2)&&(isVisible)){
                    isVisible = false;
                    UpdateLabelVisibility();
                }
            }
            else
            {
                isVisible = false;
            }
        }

        UpdateHillFrame(); // Do i need this as effectors don't have orbit lines?
        UpdateVelocityFrame();
		
        //Update spacecraft orientation
        transform.rotation = SpacecraftStateUtilities.GetSpacecraftOrientationUnityCS(spacecraftIndex);

        //Update spacecraft's primary body
        if (!SpacecraftStateUtilities.SpacecraftMsgOnly) {
            int[] bodiesToWorryAbout = OrbitVectorMath.FindPrimaryBody (spacecraftIndex,true);
            spacecraftParentBodyIndex = bodiesToWorryAbout[0];
            spacecraftNearbySmallBodyIndex = bodiesToWorryAbout[1];
        }
		
        UpdateSprite(parentSpacecraft.GetComponent<SpacecraftController>().GetSpriteOnLastFrame(), scaleToUse);
			
        SetScale (scaleToUse);
    }

    public float GetDesiredSpacecraftScale()
    {
        if (CelestialBodyStateUtilities.ViewIsLocal)
        {
            if (CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
            {
                return (float) (CelestialBodyStateUtilities.GetCurrentScale() * ratioProjectionToTrueDistanceFromCam);
            }

            return SpacecraftStateUtilities.DefaultLocalViewSpacecraftScale;
        }

        return SpacecraftStateUtilities.DefaultHelioViewSpacecraftScale;
    }

    private void SetScale(float newRadius){
        if (newRadius > 0)
        {
            transform.localScale = newRadius*Vector3.one;
        }

        if (!CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
        {
            Vector3 localScale = transform.localScale;
            antennaCollider.transform.localScale = new Vector3(0.01f/localScale.x, 0.01f/localScale.y, 0.01f/localScale.z);
        }else{
            if (spriteWasOnLastFrame){
                Vector3 localScale = transform.localScale;
                antennaCollider.transform.localScale = new Vector3(0.01f/localScale.x, 0.01f/localScale.y, 0.01f/localScale.z);
            }else{
                antennaCollider.transform.localScale = Vector3.one;
            }
        }
        BroadcastMessage("ApplySpacecraftScaleChange", newRadius, SendMessageOptions.DontRequireReceiver);
    }

    private void UpdateClickableCollider()
    {
        ModelBounds myBounds = spacecraftModel.GetComponent<ModelBounds>();
        if (myBounds == null){
            myBounds = spacecraftModel.AddComponent<ModelBounds>();
			
        }

        clickableCollider.SetActive(true);
        if (myBounds.useBoxCollider){
            if (clickableCollider.GetComponent<BoxCollider>()==null){
                clickableCollider.AddComponent<BoxCollider>();
                clickableCollider.GetComponent<BoxCollider>().isTrigger = true;
                Destroy(clickableCollider.GetComponent<SphereCollider>());
            }

            clickableCollider.GetComponent<BoxCollider>().size = 2f * myBounds.modelExtents;
            clickableCollider.GetComponent<BoxCollider>().center = myBounds.modelCenter;

        }else{
            if (clickableCollider.GetComponent<SphereCollider>()==null){
                clickableCollider.AddComponent<SphereCollider>();
                clickableCollider.GetComponent<SphereCollider>().isTrigger = true;
                Destroy(clickableCollider.GetComponent<BoxCollider>());
            }
            clickableCollider.GetComponent<SphereCollider>().radius = myBounds.modelExtents.sqrMagnitude;
            clickableCollider.GetComponent<SphereCollider>().center = myBounds.modelCenter;
        }
    }

    private void AddActuators(){
        if (MessageList.FirstMessage.Spacecraft[spacecraftIndex].ReactionWheels.Count > 0)
        {
            CreateReactionWheelsHUD ();
        }
        if (MessageList.FirstMessage.Spacecraft[spacecraftIndex].Thrusters.Count > 0)
        {
            CreateThrustersHUD ();
        }

    }

    private void CreateReactionWheelsHUD(){
		
        bool HUDon = false; //default behavior
        bool labelsOn = false; //default behavior
        if (myGUIActuatorSettings != null){
            if (myGUIActuatorSettings.ViewRWHUD == 1){
                HUDon = true;
            }
            if (myGUIActuatorSettings.ShowRWLabels ==1){
                VizardGUISettings.ShowRWLabels = true;
                labelsOn = true;
            }
        }
        //Build and add the reaction wheel heads up displays
        int wheelCount = MessageList.FirstMessage.Spacecraft[spacecraftIndex].ReactionWheels.Count;
        if (wheelCount > 0)
        {
            string HUDtype = "ReactionWheelGroup";
            GameObject reactionWheelGroup = new GameObject
            {
                name = HUDtype
            };
            reactionWheelGroup.transform.SetParent (transform);
            HUDcontainers[HUDtype] = reactionWheelGroup;
            
            for (int i = 0; i < wheelCount; i++) {
                GameObject rwHUDUnit = Instantiate(Resources.Load("Prefabs/SpacecraftHUD/ReactionWheelHUD")as GameObject, reactionWheelGroup.transform, true);
                rwHUDUnit.GetComponent<ReactionWheelHUDMethods>().InitializeReactionWheelHUDUnit(spacecraftIndex, i, meshDimension);
                rwHUDUnit.name = "ReactionWheelHUD_" + i;

                string rwName = $"RW{i}";
                Vector2 rwScreenOffset = new Vector2(10,-20);
                GameObject rwLabel=LabelMaker.CreateLabel(rwName, spacecraftName, rwHUDUnit, rwScreenOffset, "ReactionWheels");
                allMyLabels.Add(rwLabel);
                rwHUDUnit.GetComponent<ReactionWheelHUDMethods>().rwLabel = rwLabel;
                if ((!labelsOn)||(!HUDon)){
                    rwLabel.SetActive(false);
                }
            }
            reactionWheelGroup.transform.localScale = new Vector3 (1f, 1f, 1f);
            reactionWheelGroup.SetActive(HUDon);
            VizardGUISettings.PanelViewMgr.AddHUDToggle (spacecraftName, "Reaction Wheels", "HUD", reactionWheelGroup, true, HUDon, parentSpacecraftName);
        }
    }

    private void CreateThrustersHUD(){

        bool HUDon = true; //default behavior
        bool labelsOn = false; //default behavior
        if (myGUIActuatorSettings != null)
        {
            if (myGUIActuatorSettings.ViewThrusterHUD == -1)
            {
                HUDon = false;
            }

            if (myGUIActuatorSettings.ShowThrusterLabels == 1)
            {
                VizardGUISettings.ShowThrusterLabels = true;
                labelsOn = true;
            }
        }

        //Build and add the thrusters
        Dictionary<string, List<int>> thrusterGroups = ThrusterUtilities.GetThrusterGroups(spacecraftIndex);
        string HUDtype = "Thrusters";
        GameObject allThrustersContainer = new GameObject
        {
            name = HUDtype
        };
        allThrustersContainer.transform.SetParent(transform);
        HUDcontainers[HUDtype] = allThrustersContainer;

        foreach (string groupName in thrusterGroups.Keys)
        {
            List<int> thrusterGroup = thrusterGroups[groupName];
            double currentMaxThrust = 0;
				
            float particleScaleFactor = 0.05f;
            
            GameObject thrusterGroupContainer = new GameObject();
            thrusterGroupContainer.transform.SetParent(allThrustersContainer.transform);

            bool setGroupValues = false;
            foreach (int i in thrusterGroup)
            {
                float geomScaleFactor = 0.002f;
                if (setGroupValues == false)
                {
                    currentMaxThrust = MessageList.CurrentMessage.Spacecraft[spacecraftIndex].Thrusters[i]
                        .MaxThrust;
                    var currentThrusterTag = MessageList.CurrentMessage.Spacecraft[spacecraftIndex].Thrusters[i]
                        .ThrusterTag;
                    if ((currentThrusterTag == null) || (currentThrusterTag == ""))
                    {
                        thrusterGroupContainer.name = $"Thruster Group:{currentMaxThrust}";
                    }
                    else
                    {
                        thrusterGroupContainer.name = $"Thruster Group {currentThrusterTag}:{currentMaxThrust}";
                    }

                    setGroupValues = true;
                }

                GameObject thrusterHUDUnit =
                    Instantiate(Resources.Load("Prefabs/SpacecraftHUD/ThrusterHUD") as GameObject, thrusterGroupContainer.transform);
                thrusterHUDUnit.GetComponent<ThrusterHUDMethods>()
                    .InitializeThrusterHUDUnit(spacecraftIndex, i, gameObject);
                thrusterHUDUnit.name = "Thruster_" + i;
                //thrusterHUDUnit.transform.SetParent(thrusterGroupContainer.transform);
                if (MessageList.FirstMessage.Spacecraft[spacecraftIndex].Thrusters[i].MaxThrust > 0)
                {
                    geomScaleFactor *= (float) currentMaxThrust;
                }

                thrusterHUDUnit.transform.GetChild(1).gameObject.transform.localScale = geomScaleFactor * Vector3.one;
                thrusterHUDUnit.transform.GetChild(0).gameObject.transform.localScale = particleScaleFactor * Vector3.one;

                string thrusterName = $"Th{i}";
                GameObject thrusterLabel = LabelMaker.CreateLabel(thrusterName, spacecraftName, thrusterHUDUnit,
                    Vector2.zero, "Thrusters");
                allMyLabels.Add(thrusterLabel);
                thrusterHUDUnit.GetComponent<ThrusterHUDMethods>().thrusterLabel = thrusterLabel;
                if ((!labelsOn) || (!HUDon))
                {
                    thrusterLabel.SetActive(false);
                }
            }

            thrusterGroupContainer.transform.localScale = new Vector3(1f, 1f, 1f);
        }

        allThrustersContainer.transform.localScale = new Vector3(1f, 1f, 1f);
        //thrusters HUD is on by default,
        allThrustersContainer.SetActive(HUDon);

        VizardGUISettings.PanelViewMgr.AddHUDToggle(spacecraftName, "Thrusters", "HUD", allThrustersContainer, true, HUDon, parentSpacecraftName);
    }

    private void CreateLabels(){
        //Spacecraft Name
        Vector2 screenOffset = new Vector2(15,-15);
        string labelType = "Spacecraft";
        if (parentSpacecraftName != "")
        {
            labelType = "Effectors";
        }
        nameLabel = LabelMaker.CreateLabel(spacecraftName, "Label", transform.gameObject, screenOffset, labelType);

        nameLabel.SetActive(labelType == "Spacecraft"
            ? VizardGUISettings.ShowSpacecraftLabels
            : VizardGUISettings.ShowEffectorLabels);

        allMyLabels.Add(nameLabel);
        //Inertial Coordinate System
        char prefix = '\u0062';
        string xLabel = $"{prefix.ToString()}{LabelMaker.Circumflex.ToString()}<sub>1</sub>";
        string yLabel = $"{prefix.ToString()}{LabelMaker.Circumflex.ToString()}<sub>2</sub>";
        string zLabel = $"{prefix.ToString()}{LabelMaker.Circumflex.ToString()}<sub>3</sub>";
        GameObject x = LabelMaker.CreateLabel(xLabel, spacecraftName, inertialCoordinateAxes.transform.GetChild(0).gameObject, Vector2.zero, "CoordinateSystems");
        GameObject y = LabelMaker.CreateLabel(yLabel, spacecraftName, inertialCoordinateAxes.transform.GetChild(1).gameObject, Vector2.zero, "CoordinateSystems");
        GameObject z = LabelMaker.CreateLabel(zLabel, spacecraftName, inertialCoordinateAxes.transform.GetChild(2).gameObject, Vector2.zero, "CoordinateSystems");
        inertialCoordinateAxes.GetComponent<DrawAxes>().AttachCSLabels(x,y,z);
        allMyLabels.Add(x);
        allMyLabels.Add(y);
        allMyLabels.Add(z);

        //Hill Frame Coordinate System
        xLabel = "i<sub>r</sub>";
        yLabel = "i<sub>\u0275</sub>";
        zLabel = "i<sub>h</sub>";
        x = LabelMaker.CreateLabel(xLabel, spacecraftName, hillFrameCoordinateAxes.transform.GetChild(0).gameObject, Vector2.zero, "CoordinateSystems");
        y = LabelMaker.CreateLabel(yLabel, spacecraftName, hillFrameCoordinateAxes.transform.GetChild(1).gameObject, Vector2.zero, "CoordinateSystems");
        z = LabelMaker.CreateLabel(zLabel, spacecraftName, hillFrameCoordinateAxes.transform.GetChild(2).gameObject, Vector2.zero, "CoordinateSystems");
        hillFrameCoordinateAxes.GetComponent<DrawAxes>().AttachCSLabels(x,y,z);
        allMyLabels.Add(x);
        allMyLabels.Add(y);
        allMyLabels.Add(z);

        //Velocity Frame Coordinate System
        xLabel = "i<sub>n</sub>";
        yLabel = "i<sub>v</sub>";
        zLabel = "i<sub>h</sub>";
        x = LabelMaker.CreateLabel(xLabel, spacecraftName, velocityFrameCoordinateAxes.transform.GetChild(0).gameObject, Vector2.zero, "CoordinateSystems");
        y = LabelMaker.CreateLabel(yLabel, spacecraftName, velocityFrameCoordinateAxes.transform.GetChild(1).gameObject, Vector2.zero, "CoordinateSystems");
        z = LabelMaker.CreateLabel(zLabel, spacecraftName, velocityFrameCoordinateAxes.transform.GetChild(2).gameObject, Vector2.zero, "CoordinateSystems");
        velocityFrameCoordinateAxes.GetComponent<DrawAxes>().AttachCSLabels(x,y,z);
        allMyLabels.Add(x);
        allMyLabels.Add(y);
        allMyLabels.Add(z);
    }
	
    private void UpdateLabelVisibility(){
        foreach(GameObject label in allMyLabels){
            label.GetComponent<TextMeshProUGUI>().enabled = isVisible;
        }
    }

    private int CheckForVisibleInCamera(){
#if VIZARD_OPENXR
		Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cameraToUse);
		if (GeometryUtility.TestPlanesAABB(planes, clickableCollider.GetComponent<Collider>().bounds))
			return 1;
		else
		{
			return 2;
		}
#else

        Vector3 origin = cameraToUse.transform.position;
        Vector3 direction = transform.position - origin;
        int maxDistance = (int) direction.magnitude;

        if (Physics.Raycast(origin, direction, out var hit, maxDistance, layerMask)){
				
            if (transform.gameObject == hit.collider.gameObject.transform.parent.gameObject){
                return 1;
            } else{
                return 2;
            }
        }
        return 0;
#endif
    }

    private void CalculateMeshDimension()
    {
        Vector3
            size = spacecraftModel.GetComponent<ModelBounds>()
                .unitModelExtents; //(SpacecraftStateUtilities.CalculateModelBounds(spacecraftModel)).size;
        minDimension = Mathf.Min(size.x, size.y, size.z) * 2f;
        if (minDimension < 1f)
        {
            CelestialBodyStateUtilities.CalculateSpacecraftLocalViewScale();
        }

        meshDimension = Mathf.Max(new float[] {size.x, size.y, size.z});
        meshCenter = spacecraftModel.GetComponent<ModelBounds>().unitModelCenter;

        if (CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
        {
            askForCameraUpdate = true;
        }

        BroadcastMessage("ApplyMeshDimUpdate", meshDimension, SendMessageOptions.DontRequireReceiver);

        inertialCoordinateAxes.GetComponent<DrawAxes>().CalculateLineScale();
        hillFrameCoordinateAxes.GetComponent<DrawAxes>().CalculateLineScale();
        velocityFrameCoordinateAxes.GetComponent<DrawAxes>().CalculateLineScale();
    }

    private void CalculateHillFrameVectors(){
        double[] camTgtBodyPosition = SpacecraftStateUtilities.GetAbsSpacecraftPositionUnityCS (spacecraftIndex);
        double[] camTgtBodyVelocity = SpacecraftStateUtilities.GetAbsSpacecraftVelocityUnityCS (spacecraftIndex);

        double[] camTgtParentPosition = CelestialBodyStateUtilities.GetAbsolutePlanetPositionUnityCS (spacecraftParentBodyIndex);
        double[] camTgtParentVelocity = CelestialBodyStateUtilities.GetAbsPlanetVelocityUnityCS (spacecraftParentBodyIndex);

        //Note that I am also putting these vectors back into BSK CS 
        double[] rvec = new double[]{
            (-camTgtBodyPosition [2] + camTgtParentPosition [2]),
            (camTgtBodyPosition [0] - camTgtParentPosition [0]),
            (camTgtBodyPosition [1] - camTgtParentPosition [1])};

        double[] vvec = new double[]{
            (-camTgtBodyVelocity [2] + camTgtParentVelocity [2]),
            (camTgtBodyVelocity [0] - camTgtParentVelocity [0]),
            (camTgtBodyVelocity [1] - camTgtParentVelocity [1])};

        double[] HillFrameTranspose = OrbitVectorMath.TransposeMatrix(OrbitVectorMath.CalculateHillFrame(rvec, vvec));

        Vector3 v1 = OrbitVectorMath.ReturnVector3(OrbitVectorMath.ApplyTransformationMatrixToVector(HillFrameTranspose, new double[]{1,0,0}));
        Vector3 v2 = OrbitVectorMath.ReturnVector3(OrbitVectorMath.ApplyTransformationMatrixToVector(HillFrameTranspose, new double[]{0,1,0}));
        Vector3 v3 = OrbitVectorMath.ReturnVector3(OrbitVectorMath.ApplyTransformationMatrixToVector(HillFrameTranspose, new double[]{0,0,1}));

        //Convert back to Unity CS from inertial
        v1 = new Vector3(v1.y, v1.z, -v1.x);
        v2 = new Vector3(v2.y, v2.z, -v2.x);
        v3 = new Vector3(v3.y, v3.z, -v3.x);

        hillFrameAxes = new List<Vector3>{v1, v2,v3};

        hillFrameCoordinateAxes.GetComponent<DrawAxes>().ChangeAxes(v1,v2,v3);
    }

    private void UpdateVelocityFrameDisplay(){
        double[] camTgtBodyPosition = SpacecraftStateUtilities.GetAbsSpacecraftPositionUnityCS (spacecraftIndex);
        double[] camTgtBodyVelocity = SpacecraftStateUtilities.GetAbsSpacecraftVelocityUnityCS (spacecraftIndex);

        double[] camTgtParentPosition = CelestialBodyStateUtilities.GetAbsolutePlanetPositionUnityCS (spacecraftParentBodyIndex);
        double[] camTgtParentVelocity = CelestialBodyStateUtilities.GetAbsPlanetVelocityUnityCS (spacecraftParentBodyIndex);

        //Note that I am also putting these vectors back into BSK CS 
        double[] rvec = new double[]{
            (-camTgtBodyPosition [2] + camTgtParentPosition [2]),
            (camTgtBodyPosition [0] - camTgtParentPosition [0]),
            (camTgtBodyPosition [1] - camTgtParentPosition [1])};

        double[] vvec = new double[]{
            (-camTgtBodyVelocity [2] + camTgtParentVelocity [2]),
            (camTgtBodyVelocity [0] - camTgtParentVelocity [0]),
            (camTgtBodyVelocity [1] - camTgtParentVelocity [1])};

        double[] velFrameTranspose = OrbitVectorMath.TransposeMatrix(OrbitVectorMath.CalculateVelocityFrame(rvec, vvec));

        Vector3 v1 = OrbitVectorMath.ReturnVector3(OrbitVectorMath.ApplyTransformationMatrixToVector(velFrameTranspose, new double[]{1,0,0}));
        Vector3 v2 = OrbitVectorMath.ReturnVector3(OrbitVectorMath.ApplyTransformationMatrixToVector(velFrameTranspose, new double[]{0,1,0}));
        Vector3 v3 = OrbitVectorMath.ReturnVector3(OrbitVectorMath.ApplyTransformationMatrixToVector(velFrameTranspose, new double[]{0,0,1}));

        //Convert back to Unity CS from inertial
        v1 = new Vector3(v1.y, v1.z, -v1.x);
        v2 = new Vector3(v2.y, v2.z, -v2.x);
        v3 = new Vector3(v3.y, v3.z, -v3.x);

        velocityFrameCoordinateAxes.GetComponent<DrawAxes>().ChangeAxes(v1,v2,v3);
    }

    private void AddInstruments(){
        if (MessageList.FirstMessage.Spacecraft[spacecraftIndex].CSS.Count>0) {
            CreateCoarseSunSensorsHUD();
        }
        if (MessageList.FirstMessage.Spacecraft[spacecraftIndex].GenericSensors.Count>0){
            CreateGenericSensorsHUD();
        }
        if (MessageList.FirstMessage.Spacecraft[spacecraftIndex].Transceivers.Count>0){
            CreateTransceiversHUD();
        }
    }

    private void CreateCoarseSunSensorsHUD(){
        bool coverageOn = false; //default behavior
        bool boresightOn = false; //default behavior
        bool labelsOn = false; //default behavior
        if (myGUIInstrumentSettings != null){
            if (myGUIInstrumentSettings.ViewCSSCoverage==1){
                coverageOn = true;
            }
            if (myGUIInstrumentSettings.ViewCSSBoresight==1){
                boresightOn = true;
            }
            if(myGUIInstrumentSettings.ShowCSSLabels == 1){
                labelsOn = true;
                if (MessageList.CurrentMessage.Spacecraft.Count<=1){
                    VizardGUISettings.ShowCSSLabels = true;
                }
            }
        }
        //Build and add the CSS heads up displays
        int cssCount = MessageList.FirstMessage.Spacecraft[spacecraftIndex].CSS.Count;
        if (cssCount >0){
            string HUDtype = "CSSGroup";
            GameObject cssGroup = new GameObject
            {
                name = HUDtype
            };
            cssGroup.transform.SetParent(transform);
            cssGroup.transform.localScale = Vector3.one;
            HUDcontainers[HUDtype] = cssGroup;

            bool allCSSPositionsZeroed = CSSUtilities.CheckAllCSSPositionsZero(spacecraftIndex);

            for (int i = 0; i < cssCount; i++){
                GameObject cssHUDUnit = Instantiate(Resources.Load("Prefabs/SpacecraftHUD/CoarseSunSensorHUD")as GameObject, cssGroup.transform, true);
                cssHUDUnit.GetComponent<CSSHUDMethods>().InitializeCSSHUDUnit(spacecraftIndex, i, meshDimension, boresightOn, coverageOn, allCSSPositionsZeroed);
                cssHUDUnit.name = "CSS_HUD_"+i;

                string cssName = "CSS "+i;
                Vector2 cssScreenOffset = new Vector2(10,-10);
                GameObject cssLabel=LabelMaker.CreateLabel(cssName, spacecraftName, cssHUDUnit.transform.GetChild(1).GetChild(0).GetChild(0).gameObject, cssScreenOffset, "CoarseSunSensors");
                allMyLabels.Add(cssLabel);
                cssHUDUnit.GetComponent<CSSHUDMethods>().cssLabel = cssLabel;
                if ((!labelsOn)||(!boresightOn)){
                    cssLabel.SetActive(false);
                }
            }
	
            VizardGUISettings.PanelViewMgr.AddHUDToggle(spacecraftName, "Coarse Sun Sensors", "Coverage", cssGroup, false, coverageOn, parentSpacecraftName);
            VizardGUISettings.PanelViewMgr.AddHUDToggle(spacecraftName, "Coarse Sun Sensors", "Boresight", cssGroup, false, boresightOn, parentSpacecraftName);
        }
    }

    private void CreateGenericSensorsHUD()
    {
        bool labelsOn = false; //default behavior
        if ((myGUIInstrumentSettings != null)&& (myGUIInstrumentSettings.ShowGenericSensorLabels == 1)){
            labelsOn = true;
            if (MessageList.CurrentMessage.Spacecraft.Count<=1){
                VizardGUISettings.ShowGenericSensorLabels = true;
            }
        }
        int gsCount = MessageList.FirstMessage.Spacecraft[spacecraftIndex].GenericSensors.Count;
        if (gsCount>0){
            string HUDtype = "GSGroup";
            GameObject gsGroup = new GameObject
            {
                name = HUDtype
            };
            gsGroup.transform.SetParent(transform);
            gsGroup.transform.localScale = new Vector3(1f,1f,1f);
            HUDcontainers[HUDtype] = gsGroup;

            for (int i = 0; i < gsCount; i++){
                GameObject gsHUDUnit = Instantiate(Resources.Load("Prefabs/SpacecraftHUD/GenericSensorHUD")as GameObject, gsGroup.transform, true);
                GameObject gsHUDUnitLabel = gsHUDUnit.GetComponent<GenericSensorHUDMethods>().InitializeGenericSensorHUDUnit(spacecraftIndex,  i, meshDimension, labelsOn);

                allMyLabels.Add(gsHUDUnitLabel);
            }

            VizardGUISettings.PanelViewMgr.AddHUDToggle(spacecraftName, "Generic Sensors", "HUD", gsGroup, false, true, parentSpacecraftName);
        }
    }

    private void CreateTransceiversHUD(){
        bool labelsOn = false; //default behavior
        bool frustumOn = false; //default behavior;
        if (myGUIInstrumentSettings != null){
            if(myGUIInstrumentSettings.ShowTransceiverLabels == 1){
                labelsOn = true;
                if (MessageList.CurrentMessage.Spacecraft.Count<=1){
                    VizardGUISettings.ShowTransceiverLabels = true;
                }
            }
            if(myGUIInstrumentSettings.ShowTransceiverFrustum == 1){
                frustumOn = true;
            }
        }

        int txCount = MessageList.FirstMessage.Spacecraft[spacecraftIndex].Transceivers.Count;
        if (txCount>0)
        {
            string HUDtype = "TransceiverGroup";
            GameObject txGroup = new GameObject
            {
                name = HUDtype
            };
            txGroup.transform.SetParent(transform);
            txGroup.transform.localScale = new Vector3(1f,1f,1f);
            HUDcontainers[HUDtype] = txGroup;

            for (int i = 0; i < txCount; i++){
                //VizProtobufferMessage.VizMessage.Types.Transceiver txMsg = TransceiverUtilities.getTransceiverMsg(spacecraftIndex,i);
                GameObject txHUDUnit = Instantiate(Resources.Load("Prefabs/SpacecraftHUD/TransceiverHUD")as GameObject, txGroup.transform, true);
                GameObject txLabel = txHUDUnit.GetComponent<TransceiverHUDMethods>().InitializeTransceiverHUDUnit(spacecraftIndex, spacecraftName, i, meshDimension, frustumOn,labelsOn);

                allMyLabels.Add(txLabel);
            }

            VizardGUISettings.PanelViewMgr.AddHUDToggle(spacecraftName, "Transceivers", "Comm", txGroup, false, true, parentSpacecraftName);
            VizardGUISettings.PanelViewMgr.AddHUDToggle(spacecraftName, "Transceivers", "Frustum", txGroup, false, frustumOn, parentSpacecraftName);
        }
    }

    private void AddLights(){
        LightPanelMethods lightsPanel = VizardGUISettings.PanelViewMgr.lightPanel.GetComponent<LightPanelMethods>();
        VizMessage.Types.Spacecraft scMsg = MessageList.FirstMessage.Spacecraft[spacecraftIndex];
        int lightCount = scMsg.Lights.Count;
        if (lightCount>0){
            for (int i = 0; i < lightCount; i++){
                VizProtobufferMessage.VizMessage.Types.Light lightMsg = scMsg.Lights[i];
                lightsPanel.AddLightFromMessage(lightMsg, this.gameObject, spacecraftIndex, i);
            }
        }
    }

    private void AddEllipsoids()
    {
        VizMessage myMsg = MessageList.FirstMessage;
        int eCount = myMsg.Spacecraft[spacecraftIndex].Ellipsoids.Count;
        for (int i = 0; i < eCount; i++)
        {
            GameObject newEllipsoid = Instantiate(Resources.Load("Prefabs/SpacecraftHUD/EllipsoidHUD")as GameObject);
            newEllipsoid.GetComponent<EllipsoidHUDMethods>().InitializeEllipsoid(i, this.gameObject, spacecraftIndex, myMsg.Spacecraft[spacecraftIndex].Ellipsoids[i].ShowGridLines);
            VizardGUISettings.SetShellLighting(true);
            if (myMsg.Spacecraft[spacecraftIndex].Ellipsoids[i].UseBodyFrame <= 0)
            {
                usingHillFrame = true;
            }
        }
    }

    private void AddMultiShapes()
    {
        bool labelsOn = false; //default behavior
        if ((myGUIInstrumentSettings != null) &&(myGUIInstrumentSettings.ShowMultiShapeLabels == 1)){
            labelsOn = true;
            if (MessageList.CurrentMessage.Spacecraft.Count<=1){
                VizardGUISettings.ShowMSMLabels = true;
            }
            
        }
		
        VizMessage myMsg = MessageList.FirstMessage;
        int msmCount = myMsg.Spacecraft[spacecraftIndex].MultiShapes.Count;
        if (msmCount > 0)
        {
            string HUDtype = "MultiShapes";
            GameObject msmGroup = new GameObject
            {
                name = HUDtype
            };
            msmGroup.transform.SetParent(transform);
            msmGroup.transform.localScale = Vector3.one;
            HUDcontainers[HUDtype] = msmGroup;
            
            for (int i = 0; i < msmCount; i++)
            {
                GameObject newMSM =
                    Instantiate(Resources.Load("Prefabs/SpacecraftHUD/MultiShapeHUD") as GameObject);
                newMSM.GetComponent<MultiShapeHUDMethods>().InitializeMSM(i, msmGroup, spacecraftIndex);
                VizardGUISettings.SetShellLighting(true);
				
                newMSM.name = "MultiShape " + i;
                GameObject msmLabel = LabelMaker.CreateLabel("MSM "+i, spacecraftName,
                    newMSM.transform.GetChild(0).gameObject, Vector2.zero, "MultiShapes");
                newMSM.GetComponent<MultiShapeHUDMethods>().SetLabelAndLabelState(msmLabel, labelsOn);
            }
			
            VizardGUISettings.PanelViewMgr.AddHUDToggle(spacecraftName, "MultiShapes", "", msmGroup, false, true, parentSpacecraftName);
        }
    }

    public void ReplaceSpacecraftModelAndUpdate(GameObject modelToUse)
    {
        inLoad = false;
        needRemoteModel = false;
        FinalizeAppliedModel(modelToUse);
        CalculateMeshDimension();
        UpdateClickableCollider();
        UpdateAntennaCollider();
        ApplyCurrentEmissionSetting();
    }

    private void UpdateAntennaCollider()
    {
        antennaCollider.transform.position = meshCenter;
        antennaCollider.GetComponent<SphereCollider>().radius = minDimension/2f;
    }
    public void ApplyCurrentEmissionSetting(){
        Renderer[] rr = spacecraftModel.transform.GetComponentsInChildren<Renderer>();
        float value = (float) PersistentUserSettings.persistentSettingsFromLastSave.SpacecraftShadowBrightness;
        Color emissiveColor = new Color(value, value, value, 1f);
        foreach (Renderer r in rr){
            if(!r.transform.gameObject.CompareTag("ReflectiveSolarPanel")){
                Material[] materials = r.materials;
                foreach(Material myMaterial in materials){	
                    if (value < .001f){
                        myMaterial.DisableKeyword("_EMISSION");
                        myMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                        myMaterial.SetColor("_EmissionColor", Color.black);
                    }else{
                        myMaterial.EnableKeyword("_EMISSION");
                        myMaterial.EnableKeyword("_EMISSIONMAP");
                        if (myMaterial.HasTexture("_MainTex"))
                        {
                            myMaterial.SetTexture("_EmissionMap", myMaterial.GetTexture("_MainTex"));
                        }else if (myMaterial.HasTexture("baseColorTexture"))
                        {
                            myMaterial.SetTexture("emissiveTexture", myMaterial.GetTexture("baseColorTexture"));
                        }
                        else
                        {
                            Debug.Log("Could not find the main texture equivalent for this material.");
                        }

                        myMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None; //Don't want the emission to actually create light, just lighten the shadows.
                        myMaterial.SetColor("_EmissionColor", emissiveColor);
                    }
                }
            }
        }
    }

    public bool GetSpriteOnLastFrame(){
        return spriteWasOnLastFrame;
    }

    private void ApplyModelKeySetting(string key)
    {
        string keyToTry = key.ToLower();
        if (SpacecraftStateUtilities.SpacecraftModels.TryGetValue(keyToTry, out var modelInfo))
        {
            needRemoteModel = false;
            useLocalModel = true;

            //Model is in resource folder
            GameObject newModel =
                Instantiate(Resources.Load(modelInfo[0]) as GameObject);
            ReplaceSpacecraftModelAndUpdate(newModel);
        }else
        {
            needRemoteModel = true;
            useLocalModel = false;
            if (GoodEnoughAddressables.AllRemoteCatalogsLoaded)
            {
                inLoad = true;
				
                if (CelestialBodyStateUtilities.RemoteModelKeyValid(modelKey)){
                    VizardGUISettings.AddRemoteAssetLoadToList(modelKey);
                    modelHandle = Addressables.LoadAssetAsync<GameObject>(modelKey);
                    modelHandle.Completed += ModelHandleLoaded;
                }
                else
                {
                    string errMsg = $"Spacecraft model key: {modelKey} not found in Addressables bundles.";
                    VizardGUISettings.UpdateErrorMessages(errMsg);
                }
            }
        }
    }

    private void ModelHandleLoaded(AsyncOperationHandle<GameObject> operation)
    {
        if (operation.Status == AsyncOperationStatus.Succeeded)
        {
            GameObject newModel = Instantiate(operation.Result); 
            VizardGUISettings.PopRemoteAssetLoadFromList(modelKey, true);
            ChangeLayerOfAllChildren(newModel.transform, 11);
            ConfigureReflectionProbes(newModel);
            ReplaceSpacecraftModelAndUpdate(newModel);
        }
        else
        {
            VizardGUISettings.PopRemoteAssetLoadFromList(modelKey, false);
        }

        inLoad = false;
    }

    private void ChangeLayerOfAllChildren(Transform child, int layerNumber)
    {
        child.gameObject.layer = layerNumber;
        foreach (Transform grandchild in child)
        {
            ChangeLayerOfAllChildren(grandchild, layerNumber);
        }
    }

    private void ConfigureReflectionProbes(GameObject model)
    {
        ReflectionProbe[] allProbes = model.GetComponentsInChildren<ReflectionProbe>();

        if (allProbes.Length > 0)
        {
            VizardGUISettings.UpdateErrorMessages(
                $"Reflection probes on {modelKey} have been optimized for Vizard.  Box offset has not been changed.");
            foreach (ReflectionProbe probe in allProbes)
            {
                probe.mode = ReflectionProbeMode.Realtime;
                probe.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
                probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;

                probe.importance = 1;
                probe.intensity = 1;
                probe.boxProjection = false;
                probe.size = 30f * Vector3.one;

                probe.resolution = 1024;
                probe.hdr = true;
                probe.shadowDistance = 100;
                probe.clearFlags = ReflectionProbeClearFlags.SolidColor;
                probe.backgroundColor = Color.black;
                probe.cullingMask = ((1 << 0)|(1<<8)|(1<<11));
                probe.nearClipPlane = MainCameraUtilities.MainCamera.nearClipPlane;
                probe.farClipPlane = MainCameraUtilities.MainCamera.farClipPlane; 
            }
        }
        MainCameraUtilities.FindAllReflectionProbes();
    }

    private void FinalizeAppliedModel(GameObject newModel)
    {
        if (!newModel.GetComponent<ModelBounds>())
        {
            ModelBounds myBounds = newModel.AddComponent<ModelBounds>();
            myBounds.SetupUnitBoundsForModel(newModel);
            myBounds.SetupModelBoundsWithModel(myBounds.useBoxCollider, newModel);
        }
        GameObject oldModel = spacecraftModel;
        Quaternion incomingRotation = newModel.transform.localRotation;
        Vector3 incomingPosition = newModel.transform.localPosition;
        Vector3 incomingScale = newModel.transform.localScale;
        newModel.transform.SetParent(transform);
        newModel.transform.SetSiblingIndex(0);
	
        spacecraftModel = newModel;
        spacecraftModel.transform.localPosition = incomingPosition;
        spacecraftModel.transform.localRotation = incomingRotation;
        spacecraftModel.transform.localScale = incomingScale;

        Destroy(oldModel);
    }

    public void SetDefaultModel(VizMessage.Types.CustomModel newSettings){
        myModelSettings = newSettings;
    }

    public VizProtobufferMessage.VizMessage.Types.CustomModel GetDefaultModel(){
        return myModelSettings;
    }
	
    private void OnDestroy()
    {
        if (modelHandle.IsValid())
        {
            Addressables.Release(modelHandle);
        }
    }

    private Texture2D LoadAndApplyLogoTexture(string texturePath)
    {
        Texture2D customLogo = CameraMessageUtilities.LoadTextureImage(texturePath);
        if (customLogo != null)
        {
            if ((customLogo.width == 8) && (customLogo.height == 8))
            {
                string errMsg = $"Custom texture {texturePath} could not be applied. Textures must be 16384 pixels x 16384 pixels or less.";
                VizardGUISettings.UpdateErrorMessages(errMsg);
            }else{
                foreach (Transform child in spacecraftModel.transform)
                {
                    if (child.name.Contains("Decal_"))
                    {
                        child.GetComponent<MeshRenderer>().material.SetTexture("_MainTex", customLogo);
                    }
                }
            }
        }

        return customLogo;
    }

    public void UpdateDefaultSprite(string spriteSetting)
    {
        if (usingDefaultSprite)
        {
            VizardGUISettings.ApplySpriteSettingString(spriteSetting, spacecraftSprite);
        }
    }

    private void SetSpacecraftSpecificSprite(string spriteSettings, Texture2D logo = null)
    {
        if (useLocalModel)
        {
            if (!String.IsNullOrEmpty(spriteSettings))
            {
                VizardGUISettings.ApplySpriteSettingString(spriteSettings, spacecraftSprite);
                usingDefaultSprite = false;
            }
            else
            {
                if (logo != null)
                {
                    spacecraftSprite.GetComponent<SpriteRenderer>().sprite = Sprite.Create(logo,
                        new Rect(0, 0, logo.width, logo.height), new Vector2(0.5f, 0.5f));
                }
            }
        }
    }

    private void UpdateSprite(bool scBeyondThreshold, float scaleToUse)
    {
        bool turnSpriteOnThisFrame = false;

        float distanceFromCamera = (transform.position-cameraToUse.transform.position).magnitude;
        //Scarlet demo app ("MODELS a,b"): once it has chosen, a spacecraft it listed is always
        //its model and any other always a sprite, at any distance; until then Vizard's own rule.
        //An effector follows its parent spacecraft.
        int modelChoice = ScarletPresentation.ModelChoice(
            isEffector && parentSpacecraft != null ? parentSpacecraft.name : spacecraftName);
        if (VizardGUISettings.ShowSpritesForSpacecraft && modelChoice < 0)
        {
            turnSpriteOnThisFrame = true;
        }
        else if (VizardGUISettings.ShowSpritesForSpacecraft && modelChoice == 0)
        {
            if (scBeyondThreshold)
            {
                turnSpriteOnThisFrame = true;
            }else if((meshDimension*CelestialBodyStateUtilities.SpacecraftLocalViewScale*2f/(distanceFromCamera))<VizardGUISettings.SpacecraftApparentSizeThreshold)
            {
                turnSpriteOnThisFrame = true;
            }
        }
	
        if (turnSpriteOnThisFrame != spriteWasOnLastFrame){
            BroadcastMessage("ConfigureHUDForSpriteMode", turnSpriteOnThisFrame, SendMessageOptions.DontRequireReceiver);
            spacecraftModel.SetActive(!turnSpriteOnThisFrame);
            spacecraftSprite.SetActive(turnSpriteOnThisFrame&&(!isEffector));
        }

        if (turnSpriteOnThisFrame){
            float spriteScale = VizardGUISettings.CalculateScaleForSpacecraftSprite(spacecraftSprite.transform);
            spacecraftSprite.transform.localScale = Vector3.one*(spriteScale/scaleToUse);
        }
        spriteWasOnLastFrame = turnSpriteOnThisFrame;
    }

    private void UpdateHillFrame()
    {
#if VIZARD_OPENXR
		bool frameShouldBeOn = VizardGUISettings.ShowHillFrame;
#else
        bool frameShouldBeOn = VizardGUISettings.ShowHillFrame&&(spacecraftName == MainCameraUtilities.CameraTargetName);
#endif
        if ((usingHillFrame)||(frameShouldBeOn))
        {
            CalculateHillFrameVectors();
        }
        hillFrameCoordinateAxes.SetActive(frameShouldBeOn);
    }

    private void UpdateVelocityFrame()
    {
#if VIZARD_OPENXR
		bool frameShouldBeOn = VizardGUISettings.ShowVelocityFrame;
#else
        bool frameShouldBeOn = VizardGUISettings.ShowVelocityFrame&&(spacecraftName == MainCameraUtilities.CameraTargetName);
#endif
        if ((frameShouldBeOn))
        {
            UpdateVelocityFrameDisplay();
        }
        velocityFrameCoordinateAxes.SetActive(frameShouldBeOn);
    }

    public void UpdateThrusterGeometry()
    {
        ThrusterHUDMethods[] allThrusters = GetComponentsInChildren<ThrusterHUDMethods>();
        foreach (ThrusterHUDMethods thrusterMethods in allThrusters)
        {
            thrusterMethods.UpdateThrusterGeometryCone();
        }
    }
	
    public double GetRatioProjectionToTrueDistanceFromCam()
    {
        return ratioProjectionToTrueDistanceFromCam;
    }

    private void CheckForEclipsed()
    {
        int eclipseLayerMask = (1 << 9); 
        Vector3 direction = new Vector3(1,0,0); //This is toward the direction the no-sun direction light is coming from.
        Vector3 origin = transform.position;
        if (CelestialBodyStateUtilities.SunMsgAvailable)
        {
            direction = CelestialBodyStateUtilities.SunTransform.position - origin;
        }
		
        if (Physics.Raycast(origin,direction, out var hit, MainCameraUtilities.MainCamera.farClipPlane, eclipseLayerMask))
        {
            GameObject hitObject = hit.collider.gameObject.transform.parent.gameObject;
            if (!hitObject.CompareTag("Sun"))
            {
                if (spacecraftModel.layer == 11)
                {
                    SetLayerAllChildren(spacecraftModel.transform, 7);
                }
            }
            else
            {
                if (spacecraftModel.layer == 7)
                {
                    SetLayerAllChildren(spacecraftModel.transform, 11);
                }
            }
        }
        else
        {
            if (spacecraftModel.layer == 7)
            {
                SetLayerAllChildren(spacecraftModel.transform, 11);
            }
        }
    }
	
    void SetLayerAllChildren(Transform root, int layer)
    {
        var children = root.GetComponentsInChildren<Transform>(includeInactive: true);
        foreach (var child in children)
        {
            child.gameObject.layer = layer;
        }
    }

    public float GetMeshOffsetForMainCamera()
    {
        float meshSize = meshDimension;
        if (minDimension < 1f)
        {
            meshSize *= 1f / minDimension;
        }

        return meshSize;
    }

    public int GetParentSpacecraftIndex()
    {
        return parentSpacecraftIndex;
    }

    public GameObject GetHUDContainer(string HUDtype)
    {
        if (HUDcontainers.ContainsKey(HUDtype))
        {
            return HUDcontainers[HUDtype];
        }
        return null;
    }

}