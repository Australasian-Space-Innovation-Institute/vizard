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

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
/// <summary>
/// Maintains static variables and methods used to move the camera rig per user input,
/// trigger view transitions, and set the current view scale
/// </summary>
public static class MainCameraUtilities 
{
    //Cameras
    public static Camera MainCamera; //Vizard Main Scene Camera
    public static List<Camera> SecondaryCameras = new List<Camera>(); //All secondary cameras (instrument and standard) in Vizard Main scene
    public static List<GameObject> InstrumentCameras = new List<GameObject>(); //All instrument cameras in Vizard Main scene
    private static List<ReflectionProbe> reflectionProbesInScene; //All reflection probes in Vizard Main scene
    
    //Main camera target
    public static GameObject CameraTarget;  //Current main camera target
    public static string CameraTargetName;  //Name of main camera target object
    public static int CameraTargetIndex;    //Index of main camera target object in VizMessage.Spacecraft[] or VizMessage.CelestialBodies[]
    public static bool CameraTargetIsSpacecraftOrEffector; //True if current main camera target is a spacecraft or effector
    public static int CameraTargetParentBodyIndex;  //Index of camera target's parent body in VizMessage.CelestialBodies[]
    public static string SceneDefaultTarget; //Target the scene started with, for "CAMERA" requests with no name

    //Range to main target
    private static double trueCameraDistanceToTargetMeters; //[meters] True distance from camera to camera target in meters (spacecraft local view) (when distance is greater than projection wall, this value will be increased without moving camera and used to scale all objects
    
    //View threshold settings
    public static float SpacecraftLocalTransitionBoundaryUnityUnits = 5000.0f; // [Unity Units] Distance from camera target in spacecraft local view at which transition to planet local view is triggered
    public static float PlanetLocalTransitionBoundaryUnityUnits = 20000.0f;// [Unity Units] Distance from camera target in planet local view at which transition to solar system wide view is triggered
    
    public static double DistanceToProjectionWallUnityUnits = 1000; //[Unity Units] Distance from camera target to projection wall for distance objects in spacecraft and planet local views
    public static double ProjectionWallStackingConstantUnityUnits = 10000; //[Unity Units]

    public static float LineAndSpriteProjectionCorrectionThreshold = 1000; //[Unity Units] Use parent body ratioProjectionToTrueDistanceFromCam to project orbit lines and sprites beyond this threshold in spacecraft local view

    public static bool ForceSpacecraftLocalView; //Prevent zoom-out to planet local by ignoring spacecraft local threshold
    public static bool SpacecraftLocalViewScaleChanged;  // True if the spacecraft local scale has changed

    //Mouse (touchpad) controlled movement constants
    public static float UserInputZoomFactor = 0.05f; //Controls how much the camera zooms in or out per frame when scrolling
    public static float UserInputRotateSpeed = 10f; //Speed the camera rotates about target on drag
    
    //Keyboard controlled camera movements ("Movie Controls")
    public static int KeyHorizPan; // Current multiplier for keyboard horizontal pan
    public static int KeyVertPan; // Current multiplier for keyboard vertical pan
    public static int KeyRoll; // Current multiplier for keyboard roll
    public static int KeyZoom; // Current multiplier for keyboard zoom
    public static float KeyZoomMultiplier = 1f; // Multiplier for keyboard controlled zoom rate (can be set in VizMessage.Settings)
    public static float KeyZoomRate = 0.004f; //Fraction of distance to camera to zoom in our out
    public static float KeyPanRate = 2f; // Multiplier for keyboard controlled pan rate (can be set in VizMessage.Settings)

    //Unit test support variables
    public static bool CameraInUnitTestMode; //Place main camera in unit test mode
    public static double[] UnitTestCameraPosition = new double[3]; //Set position of camera in unit test mode
    
    
    public static double TrueCameraDistanceToTargetMeters
    {
        get{
            return trueCameraDistanceToTargetMeters;
        }
        set => trueCameraDistanceToTargetMeters = value;
    }
    
    /// <summary>
    /// Returns where the position of the camera rig
    /// from the camera target would be in meters in
    /// the Unity left-hand frame using the trueCameraDistance
    /// </summary>
    /// <returns>Position of main camera in meters in Unity Left-Handed Frame</returns>
    public static double[] GetAbsoluteMainCameraPositionInMeters()
    {
        if (CameraInUnitTestMode)
        {
            return UnitTestCameraPosition;
        }

        Vector3 camPosition = MainCamera.gameObject.transform.position.normalized;
        return new [] {camPosition.x*TrueCameraDistanceToTargetMeters, camPosition.y*TrueCameraDistanceToTargetMeters, camPosition.z*TrueCameraDistanceToTargetMeters};
    }
    
    /// <summary>
    /// Returns the position for the requested object
    /// relative to the main camera target 
    /// in Unity Units for the current view scale
    /// </summary>
    /// <param name="bodyIndex">VizMessage.Spacecraft[] or VizMessage.CelestialBodies[] index of requested object</param>
    /// <param name="isSpacecraft">True if the requested object is a spacecraft or effector</param>
    /// <returns>Position of body relative to camera target in Unity Units in current view scale</returns>
    public static double[] GetScaledObjectPositionRelToCamTgt(int bodyIndex, bool isSpacecraft=false)
    {
        // Raw body position in meters from VizMessage rotated into Unity CS
        double[] outPositionUnity = (isSpacecraft? SpacecraftStateUtilities.GetAbsSpacecraftPositionUnityCS(bodyIndex): CelestialBodyStateUtilities.GetAbsolutePlanetPositionUnityCS(bodyIndex));
        // Raw position in meters of camera target from VizMessage rotated into Unity CS
        double[] cameraTargetPositionUnity = GetCameraTargetAbsolutePositionUnityCS();
        // Relative position in meters of body from camera target
        outPositionUnity = OrbitVectorMath.Subtract(outPositionUnity, cameraTargetPositionUnity);
        // Current view scale meters to Unity Units
        double scaleToUse = CelestialBodyStateUtilities.GetCurrentScale();
        if (!CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
        {
            scaleToUse = 1 / scaleToUse;
        }
        //Scale the relative position in meters to get Unity Units
        outPositionUnity = OrbitVectorMath.ScaleVector(outPositionUnity, scaleToUse);
        
        return outPositionUnity;
    }
    
    /// <summary>
    /// Get the current VizMessage position of the camera target object in meters, rotated into the Unity Frame
    /// </summary>
    /// <returns>Position of camera target in meters rotated into the Unity Left-Handed Frame</returns>
    public static double[] GetCameraTargetAbsolutePositionUnityCS ()
    {
        double[] targetPosition = GetCameraTargetAbsolutePositionBSK();
        targetPosition = OrbitVectorMath.TransformFromBSKCStoUnity(targetPosition);
        return targetPosition;
    }
    
    /// <summary>
    /// Get the current VizMessage position of the camera target object in meters in the BSK Frame
    /// </summary>
    /// <returns>Position of the camera target in meters in the BSK Right-Handed Frame</returns>
    public static double[] GetCameraTargetAbsolutePositionBSK (){
        double[] targetPosition;
        //Get current target position from message
        if (CameraTargetIsSpacecraftOrEffector)
        {
            // Target is a spacecraft or effector
            targetPosition = MessageList.CurrentMessage.Spacecraft[CameraTargetIndex].Position.ToArray();
        }
        else
        {
            //Check if the camera target it the origin target, otherwise it is a celestial body
            targetPosition = CameraTarget.CompareTag("OriginTarget")
                ? new double[] {0, 0, 0}
                : MessageList.CurrentMessage.CelestialBodies[CameraTargetIndex].Position.ToArray();
        }
        return targetPosition;
    }
    
/// <summary>
/// Set the CelestialBodyStateUtilities view variables for planet local view scale
/// </summary>
    public static void LocalViewRequested ()
    {
        CelestialBodyStateUtilities.CalculateLocalPlanetViewScale(CameraTarget);
        CelestialBodyStateUtilities.ViewIsLocal = true;
        CelestialBodyStateUtilities.ViewIsSpacecraftLocal = false;
    }

    /// <summary>
    /// Set the CelestialBodyStateUtilities view variables for solar system (helio) view scale
    /// </summary>
    public static void SolarSystemViewRequested()
    {
        CelestialBodyStateUtilities.ViewIsLocal = false;
        CelestialBodyStateUtilities.ViewIsSpacecraftLocal = false;
        
        // Turn off any HD atmosphere shader materials
        foreach (GameObject planet in CelestialBodyStateUtilities.CelestialBodiesList)
        {
            if (!planet.CompareTag("Sun")){
                planet.GetComponent<PlanetController>().EnableAtmosphereCalculations(false, true);
            }
        }
    }
    /// <summary>
    /// Set the CelestialBodyStateUtilities view variables for spacecraft local view scale
    /// </summary>
    public static void SpacecraftLocalViewRequested ()
    {
        CelestialBodyStateUtilities.CalculateSpacecraftLocalViewScale();
        CelestialBodyStateUtilities.ViewIsLocal = true;
        CelestialBodyStateUtilities.ViewIsSpacecraftLocal = true;
    }

    /// <summary>
    /// Find all the reflection probes current in scene and update their clipping planes
    /// </summary>
    public static void FindAllReflectionProbes()
    {
        ReflectionProbe[] tempReflectionProbesInScene = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
        reflectionProbesInScene = tempReflectionProbesInScene.ToList();
        SetReflectionProbeAndSecondaryCameraClippingPlanes();
    }
    
    /// <summary>
    /// Set the near and far clipping planes of all reflection probes
    /// and secondary cameras to main camera's current clipping plane settings.
    /// </summary>
    public static void SetReflectionProbeAndSecondaryCameraClippingPlanes()
    {
        float nearClipPlane = MainCamera.nearClipPlane;
        float farClipPlane = MainCamera.farClipPlane;

        if (reflectionProbesInScene.Count>0)
        {
            for(int i = 0; i < reflectionProbesInScene.Count; i++)
            {
                if (reflectionProbesInScene[i]!=null)
                {
                    reflectionProbesInScene[i].nearClipPlane = nearClipPlane;
                    reflectionProbesInScene[i].farClipPlane = farClipPlane;
                }
            }
        }

        if (SecondaryCameras.Count > 0)
        {
            for (int i = 0; i < SecondaryCameras.Count; i++)
            {
                if (SecondaryCameras[i] != null)
                {
                    SecondaryCameras[i].nearClipPlane = nearClipPlane;
                    SecondaryCameras[i].farClipPlane = farClipPlane;
                }
            }
        }
    }
    
    /// <summary>
    /// Update all instrument cameras when the view scale changes
    /// </summary>
    public static void UpdateAllInstrumentCameras()
    {
        foreach(GameObject instCam in InstrumentCameras){
            instCam.GetComponent<InstrumentCameraMethods>().UpdateCameraParametersForScaleChange();
        }
    }
    
    /// <summary>
    /// Apply VizMessage.Settings for keyboard camera panning and zooming
    /// </summary>
    /// <param name="panRate">Desired setting for keyboard controlled camera panning</param>
    /// <param name="zoomRate">Desired setting for keyboard controlled camera zooming</param>
    public static void ApplyVizMessageKeyboardRateSettings(float panRate, float zoomRate){
        if (panRate>0){
            KeyPanRate = panRate;
        }
        if (zoomRate>0){
            KeyZoomMultiplier = zoomRate;
        }
    }
    
        
    /// <summary>
    /// Toggle the directional light attached to the camera rig
    /// </summary>
    public static void ToggleFlashlight()
    {
        GameObject flashlight = MainCamera.GetComponent<MainCameraMovementController>().flashlight;
        flashlight.SetActive(!flashlight.activeSelf);
    }

    /// <summary>
    /// Resets all MainCameraUtilities to defaults
    /// <remarks>Reset is called when a new scenario playback file is loaded</remarks>
    /// </summary>
    public static void ResetMainCameraUtilities()
    {
        SecondaryCameras = new List<Camera>();
        InstrumentCameras = new List<GameObject>();
        reflectionProbesInScene = new List<ReflectionProbe>();
        CameraTarget = null;
        CameraTargetName ="";
        CameraTargetIndex=0;
        CameraTargetIsSpacecraftOrEffector = false;
        CameraTargetParentBodyIndex = 0;
        SceneDefaultTarget = null;
        trueCameraDistanceToTargetMeters = 0;
        SpacecraftLocalTransitionBoundaryUnityUnits = 5000.0f; 
        PlanetLocalTransitionBoundaryUnityUnits = 20000.0f;
        DistanceToProjectionWallUnityUnits = 1000; 
        ProjectionWallStackingConstantUnityUnits = 10000; 
        LineAndSpriteProjectionCorrectionThreshold = 1000;
        ForceSpacecraftLocalView = false;
        SpacecraftLocalViewScaleChanged = false;
        UserInputZoomFactor = 0.05f; 
        UserInputRotateSpeed = 10f; 
        KeyHorizPan = 0; 
        KeyVertPan = 0; 
        KeyRoll = 0; 
        KeyZoom = 0; 
        KeyZoomMultiplier = 1f; 
        KeyZoomRate = 0.004f; 
        KeyPanRate = 2f;
        CameraInUnitTestMode = false;
        UnitTestCameraPosition = new double[3];
    }
}
