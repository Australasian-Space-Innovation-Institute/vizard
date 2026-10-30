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

using System.Linq;
using UnityEngine;

/// <summary>
/// Manages transition to different view scales, maintains camera offset
/// and up-direction, and (if cameraTarget is a spacecraft) it maintains
/// camera following in the Hill Frame
/// </summary>
public class MainCameraViewManager : MonoBehaviour
{
    public MainCameraMovementController mainCameraMovementController; // Main camera's movement controller
    public Transform cameraRigTransform; // Transform of the camera rig

    //Flags for completing startup of camera
    private bool firstUpdate = true; //True until first Update call 
    private bool startupComplete; //True if the initial camera targeting setup has been completed

    //View Transition Flags
    [HideInInspector] public bool triggerZoomTransitionToHelioView;
    [HideInInspector] public bool triggerZoomTransitionToLocalView;

    //Non-Hill Frame camera following (celestial body camera target)
    private Vector3 offsetUnity = new Vector3(35, 35, 35); //offset of camera from camera target
    private Vector3 cameraUp = new Vector3(0, 1, 0); //up vector of main camera

    //Hill Frame camera following (spacecraft camera target)
    protected double[] HillFrame =  {1, 0, 0, 0, 1, 0, 0, 0, 1}; //Current Hill Frame DCM
    protected double[] OffsetInHillFrameBSK; //Offset from camera target in the Hill Frame
    private double[] upHillFrameBSK; //Up direction of the main camera in the Hill Frame

    /// <summary>
    /// Monodevelop method called before any Start calls
    /// </summary>
    void Awake()
    {
        #if VIZARD_OPENXR
        mainCameraMovementController = GetComponent<VizardVR_MainCameraMovementController>();
        #else
        mainCameraMovementController = GetComponent<MainCameraMovementController>();
    #endif
        cameraRigTransform = mainCameraMovementController.cameraRigTransform;
    }

    /// <summary>
    /// Monodevelop method called at the FixedUpdate rate (less frequent than Update)
    /// <remarks>Used to update Hill Frame and transition to different views</remarks>
    /// <remarks>Keeping this in FixedUpdate stops jitter of bodies in main camera view</remarks>
    /// </summary>
    void FixedUpdate()
    {
        if (firstUpdate)
        {
            //Calculate the current local planet view scale 
            CelestialBodyStateUtilities.CalculateLocalPlanetViewScale(MainCameraUtilities.CameraTarget);
            firstUpdate = false;
        }

        // If the spacecraft local view scale change flag is true, request updates to 
        // thruster and instrument camera HUD objects
        if (MainCameraUtilities.SpacecraftLocalViewScaleChanged)
        {
            MainCameraUtilities.UpdateAllInstrumentCameras();
            SpacecraftStateUtilities.UpdateThrusterGeometry();
            MainCameraUtilities.SpacecraftLocalViewScaleChanged = false;
        }

        // Camera will follow spacecraft/effector camera target in its Hill Frame 
        if (MainCameraUtilities.CameraTargetIsSpacecraftOrEffector)
        {
            CalculateTargetHillFrame();
        }

        //Check for transition to solar system (helio) view scale
        if (triggerZoomTransitionToHelioView)
        {
            ZoomOutTransitionToHelioView();
        }
        //Check for transition to planet local view scale
        else if (triggerZoomTransitionToLocalView)
        {
            ZoomOutTransitionToLocalView();
        }
        else //Update camera rig position and repoint camera at camera target
        {
            UpdateInertialUnityOffset();
            cameraRigTransform.position =
                (MainCameraUtilities.CameraTarget.transform).position - offsetUnity;
            GetUp();
            cameraRigTransform.LookAt(MainCameraUtilities.CameraTarget.transform, cameraUp);
        }

        // Release the user input controlled main camera movement
        mainCameraMovementController.waitUntilCamTransitionComplete = false;
    }

    /// <summary>
    /// Called when camera has been zoomed out beyond planet local view transition threshold,
    /// forces view change to helio view (solar system wide)
    /// </summary>
    protected void ZoomOutTransitionToHelioView()
    {
        //Set view static variables for helio view scale
        CelestialBodyStateUtilities.ViewIsLocal = false;
        CelestialBodyStateUtilities.ViewIsSpacecraftLocal = false;

        //Turn off all moon bodies and moon orbit lines
        foreach (GameObject moon in CelestialBodyStateUtilities.MoonsList)
        {
            if (MainCameraUtilities.CameraTarget != moon)
            {
                moon.GetComponent<PlanetController>().orbitLine.SetActive(false);
                moon.SetActive(false);
            }
        }

        // Turn off HD atmospheres (they only work with directional lighting, helio view is point lit
        foreach (GameObject body in CelestialBodyStateUtilities.CelestialBodiesList)
        {
            if (body.CompareTag("Planet"))
            {
                //Toggle HD atmospheres off (they don't work with point light)
                body.GetComponent<PlanetController>().EnableAtmosphereCalculations(false, true);
            }
        }

        //Close out setting new camera offset
        CompleteZoomOutTransition(400f);

        // Mark transition complete
        triggerZoomTransitionToHelioView = false;
    }

    /// <summary>
    /// Called when camera has been zoomed out beyond spacecraft local view transition threshold,
    /// forces view change to planet local view
    /// </summary>
    protected void ZoomOutTransitionToLocalView()
    {
        //Set view static variables for planet local view scale
        CelestialBodyStateUtilities.ViewIsLocal = true;
        CelestialBodyStateUtilities.ViewIsSpacecraftLocal = false;

        //Set camera offset from camera target spacecraft for planet local view
        SpacecraftController spacecraftController =
            MainCameraUtilities.CameraTarget.GetComponent<SpacecraftController>();
        float desiredScale = spacecraftController.GetDesiredSpacecraftScale();
        float meshSize = spacecraftController.GetMeshOffsetForMainCamera();

        float offsetMultiplier = meshSize * 2f * desiredScale;

        //Close out setting new camera offset
        CompleteZoomOutTransition(offsetMultiplier);

        //Mark transition complete
        triggerZoomTransitionToLocalView = false;
    }

    /// <summary>
    /// Finalize camera rig offset, recalibrate camera clipping planes,
    /// calculate true distance in meters of camera from target,
    /// and update line renderer settings for the new view scale.
    /// </summary>
    /// <param name="offsetMultiplier"> Desired distance (Unity Units) from camera target</param>
    private void CompleteZoomOutTransition(float offsetMultiplier)
    {
        //Set the desired offset of the camera from the target in the new view scale
        UpdateInertialUnityOffset();
        Vector3 unitOffset = Vector3.Normalize(offsetUnity);
        cameraRigTransform.position =
            (MainCameraUtilities.CameraTarget.transform).position - offsetMultiplier * unitOffset;
        SetOffset();

        //Set clipping planes for planet local scale
        AdjustClippingPlanes(true);

        //Alert user to view transition with status text box
        VizardGUISettings.DisplayTextInFadingStatusTextBox(triggerZoomTransitionToLocalView
            ? "Planet Local View"
            : "Solar System View");

        //Calculate distance in meters between camera target and main camera
        MainCameraUtilities.TrueCameraDistanceToTargetMeters=
            ((MainCameraUtilities.CameraTarget.transform).position - cameraRigTransform.position).magnitude *
            CelestialBodyStateUtilities.LocalPlanetViewScale;

        //Update all line renderers for new view scale
        VizardGUISettings.UpdateAllLineRenderers();
    }

    /// <summary>
    /// Determine if the camera rig remains within the current view scale distance to target threshold
    /// </summary>
    /// <param name="camDistanceToTargetUnityUnits">Distance between camera rig and camera target in Unity Units</param>
    /// <returns></returns>
    public bool CameraWithinCurrentViewBoundary(float camDistanceToTargetUnityUnits)
    {
        // if spacecraft local view is the current view
        if (CelestialBodyStateUtilities.ViewIsSpacecraftLocal)
        {
            //If not locked in spacecraft local view by the user setting "forceStartInSpacecraftLocalView"
            if (!MainCameraUtilities.ForceSpacecraftLocalView)
            {
                //If distance to spacecraft camera target exceeds the spacecraft local view threshold distance
                if ((!SpacecraftStateUtilities.SpacecraftMsgOnly) &&
                    (MainCameraUtilities.TrueCameraDistanceToTargetMeters >
                     MainCameraUtilities.SpacecraftLocalTransitionBoundaryUnityUnits /
                     CelestialBodyStateUtilities.SpacecraftLocalViewScale))
                {
                    //Camera is beyond spacecraft local view boundary, force transition to planet local or solar system view
                    triggerZoomTransitionToLocalView = true;

                    //Check to see if the parent body is the sun, if yes, go straight to helio view
                    if (MainCameraUtilities.CameraTarget.GetComponent<SpacecraftController>()
                            .spacecraftParentBodyIndex ==
                        CelestialBodyStateUtilities.SunIndex)
                    {
                        triggerZoomTransitionToHelioView = true;
                        triggerZoomTransitionToLocalView = false;
                    }

                    return false; //camera rig is outside current view bounds, trigger transition
                }
            }
        }
        //if planet local view is the current view
        else
        {
            // if there is a sun to zoom out to solar system view scale and the planet local scale distance has been exceeded by camera rig
            if (CelestialBodyStateUtilities.SunMsgAvailable && CelestialBodyStateUtilities.ViewIsLocal &&
                (camDistanceToTargetUnityUnits > MainCameraUtilities.PlanetLocalTransitionBoundaryUnityUnits))
            {
                triggerZoomTransitionToHelioView = true;
                return false; //camera rig is beyond current view's bounds, transition triggered
            }
        }

        return true; //camera rig remains within current view bounds
    }

    /// <summary>
    /// Set the clipping planes of all scene cameras to contain the scale of the current view
    /// </summary>
    /// <param name="zoomIn">True if camera rig is moving closer to camera target</param>
    public void AdjustClippingPlanes(bool zoomIn)
    {
        float camDistToTarget = cameraRigTransform.position.magnitude;
        bool setReflectionProbeAndSecondaryCameraClippingPlanes = false;
        float minClippingPlane = CelestialBodyStateUtilities.ViewIsSpacecraftLocal ? 0.1f : 10f;
        //If zooming in, reduce clipping planes to keep camera target within frustum
        if (zoomIn)
        {
            while ((camDistToTarget < MainCameraUtilities.MainCamera.nearClipPlane * 10f) &&
                   (MainCameraUtilities.MainCamera.nearClipPlane > minClippingPlane))
            {
                MainCameraUtilities.MainCamera.nearClipPlane /= 10f;
                MainCameraUtilities.MainCamera.farClipPlane /= 10f;
                setReflectionProbeAndSecondaryCameraClippingPlanes = true;
            }
        }
        //If zooming out, increase clipping planes to keep camera target within frustum
        else
        {
            while (camDistToTarget > MainCameraUtilities.MainCamera.farClipPlane / 10f)
            {
                MainCameraUtilities.MainCamera.nearClipPlane *= 10f;
                MainCameraUtilities.MainCamera.farClipPlane *= 10f;
                setReflectionProbeAndSecondaryCameraClippingPlanes = true;
            }
        }

        //Update all secondary cameras and reflection probes to current clipping plane settings
        if (setReflectionProbeAndSecondaryCameraClippingPlanes)
        {
            MainCameraUtilities.SetReflectionProbeAndSecondaryCameraClippingPlanes();
        }
    }

    /// <summary>
    /// Apply VizMessage.Settings specified main camera target
    /// </summary>
    /// <param name="targetName">Name of scenario object to set as main camera target</param>
    public void ApplyVizMessageCameraTarget(string targetName)
    {
        GameObject newTarget = null;
        if (targetName != "")
        {
            // Retrieve the game object that was created for the specified scenario object name
            newTarget = CelestialBodyStateUtilities.GetGameObjectWithBodyName(targetName);
        }

        // If the targetName could not be matched to a GameObject, set the the desired camera target to 
        // the first spacecraft scenario object in VizMessage.Spacecraft[]
        if (newTarget == null)
        {
            newTarget = SpacecraftStateUtilities.SpacecraftList[0];
            VizardGUISettings.UpdateErrorMessages(
                "MainCameraTarget requested in Settings message not found. No body matching the name " +
                targetName + " found in messages.", true);
        }

        //Turn on coordinate frame of camera target if user settings demand it
        if (newTarget.CompareTag("Spacecraft"))
        {
            if (VizardGUISettings.AllSpacecraftCSOn)
            {
                VizardGUISettings.CameraTargetCSOn = true;
            }
        }
        else if (VizardGUISettings.AllPlanetCSOn)
        {
            VizardGUISettings.CameraTargetCSOn = true;
        }
        // Calculate the desired offset for the main camera from the target
        // and set the newTarget to be the main camera target
        SetupChangeOfMainCameraTarget(newTarget);
    }

    /// <summary>
    /// Adjust scene settings for new camera target
    /// </summary>
    /// <param name="newTarget">Scenario object that is to be set as the main camera target</param>
    public void SetupChangeOfMainCameraTarget(GameObject newTarget)
    {
        if (newTarget != null)
        {
            VizardCommandLog.Log("camera_target", $"target={VizardCommandLog.Value(newTarget.name)}");
            bool newTargetIsSC = false;
            if (!newTarget.CompareTag("OriginTarget"))
            {
                //Lock out movement of the main camera while in camera target change
                mainCameraMovementController.waitUntilCamTransitionComplete = true;
                newTargetIsSC = newTarget.CompareTag("Spacecraft");

                // Turn off all moons if the new camera target is the Sun
                bool moonsOn = !newTarget.CompareTag("Sun");
                foreach (GameObject moon in CelestialBodyStateUtilities.MoonsList)
                {
                    moon.SetActive(moonsOn);
                    moon.GetComponent<PlanetController>().orbitLine.SetActive(moonsOn);
                }

                // Get the VizMessage.Spacecraft[] or VizMessage.CelestialBodies[] index for the new target
                int newTargetIndex = newTargetIsSC ? newTarget.GetComponent<SpacecraftController>().spacecraftIndex : 
                    CelestialBodyStateUtilities.GetCelestialBodyIndex(newTarget.name);

                // Turn on HD atmosphere of new camera target or parent body of camera target, turn off all others
                if (newTargetIndex != -1)
                {
                    CelestialBodyStateUtilities.AdjustAtmosphereSettingsForNewCameraTarget(newTarget,
                        startupComplete ? MainCameraUtilities.CameraTarget : newTarget, newTargetIsSC);
                }

                // Flag the true path line calculations to update
                VizardGUISettings.RelativeTruePathChangeCount++;
            }

            // Finalize the change to the new camera target
            ApplyMainCameraTargetChange(newTarget, newTargetIsSC);
        }
    }

    /// <summary>
    /// Calculate main camera offset from target for a given target and view
    /// </summary>
    /// <param name="newTarget">GameObject to set as new main camera target</param>
    /// <param name="newTargetIsSpacecraft">True if the new camera target is a spacecraft or effector</param>
    private void ApplyMainCameraTargetChange(GameObject newTarget, bool newTargetIsSpacecraft)
    {
        MainCameraUtilities.CameraTarget = newTarget;
        MainCameraUtilities.CameraTargetName = newTarget.name;
        MainCameraUtilities.CameraTargetIsSpacecraftOrEffector = newTargetIsSpacecraft;
        double currentScale = 1f;
        if (MainCameraUtilities.CameraTargetIsSpacecraftOrEffector) //Spacecraft offsets
        {
            MainCameraUtilities.CameraTargetIndex = newTarget.GetComponent<SpacecraftController>().spacecraftIndex;
            //Transition to spacecraft local view scale
            MainCameraUtilities.SpacecraftLocalViewRequested();
            // If there is a celestial body in the scenario, set the camera to point
            // through spacecraft target to parent body in Hill Frame
            if (!SpacecraftStateUtilities.SpacecraftMsgOnly)
            {
                PointCameraAlongSpacecraftToPrimaryBody();
            }
            else //There is no celestial body in the scenario
            {
                cameraRigTransform.position =
                    -(newTarget.GetComponent<SpacecraftController>().meshDimension * newTarget.transform.localScale.x) *
                    Vector3.one;
                SetOffset();
                cameraRigTransform.LookAt(MainCameraUtilities.CameraTarget.transform);
            }

            currentScale = 1 / CelestialBodyStateUtilities.SpacecraftLocalViewScale;
        }
        else //Planet, Moon, Sun or origin offsets
        {
            //If new target is a planet or moon, set up planet local view 
            if (newTarget.CompareTag("Planet"))
            {
                MainCameraUtilities.CameraTargetIndex = newTarget.GetComponent<PlanetController>().planetIndex;
                MainCameraUtilities.LocalViewRequested();
                //If sun is included in scenario:
                //Set camera view of planet such that sun is behind camera
                if (CelestialBodyStateUtilities.SunMsgAvailable)
                {
                    offsetUnity = OrbitVectorMath.ReturnVector3(OrbitVectorMath.ScaleVector(
                        OrbitVectorMath.Normalized(
                            MainCameraUtilities.GetScaledObjectPositionRelToCamTgt(
                                CelestialBodyStateUtilities.SunIndex)), -350));
                    offsetUnity.y = 5;
                }
                else //Use preselected offset that will put camera on side of planet illuminated by directional light
                {
                    offsetUnity = new Vector3(-350, 5, -350);
                }

                currentScale = CelestialBodyStateUtilities.LocalPlanetViewScale;
            }
            // If new camera target is the sun, set up solar system (helio) view
            else if (newTarget.CompareTag("Sun"))
            {
                MainCameraUtilities.CameraTargetIndex = CelestialBodyStateUtilities.SunIndex;
                MainCameraUtilities.SolarSystemViewRequested();
                offsetUnity = new Vector3(-300, -300, -300);
                currentScale = CelestialBodyStateUtilities.HelioCenteredViewScale;
                triggerZoomTransitionToHelioView=true;
            }
            // The origin target coordinate frame (available when no celestial bodies are in scenario) is the camera target
            else if (newTarget.CompareTag("OriginTarget"))
            {
                MainCameraUtilities.CameraTargetIndex = -1;
                MainCameraUtilities.SpacecraftLocalViewRequested();
                float offsetConstant = (float) CelestialBodyStateUtilities.SpacecraftLocalViewScale *
                                       SpacecraftStateUtilities.SpacecraftList[0].GetComponent<SpacecraftController>()
                                           .meshDimension;
                offsetUnity = offsetConstant * new Vector3(-4.2f, -2.1f, 4.9f);
                VizardGUISettings.AllPlanetCSOn = true;
                VizardGUISettings.CameraTargetCSOn = true;
                newTarget.transform.GetChild(2).gameObject.SetActive(true);
                currentScale = 1 / CelestialBodyStateUtilities.SpacecraftLocalViewScale;
            }

            cameraRigTransform.position = -offsetUnity;
            SetOffset();
            cameraRigTransform.LookAt(MainCameraUtilities.CameraTarget.transform);
        }

        //Calculate the distance in meters of the main camera from the new camera target
        MainCameraUtilities.TrueCameraDistanceToTargetMeters=currentScale *
                                                                ((MainCameraUtilities.CameraTarget.transform).position -
                                                                 cameraRigTransform.position).magnitude;
        //Update the scale of coordinate frames and other line renderer objects for the new view scale
        VizardGUISettings.UpdateCSandLineRenderers();
        //Do not allow the main camera to take user movement input until the view transition is complete
        mainCameraMovementController.waitUntilCamTransitionComplete = true;
        startupComplete = true; //True if all the startup targeting has been completed
    }

    /// <summary>
    /// Point the main camera along the line through the spacecraft to its parent body in the Hill Frame
    /// </summary>
    private void PointCameraAlongSpacecraftToPrimaryBody()
    {
        // Get the size of the spacecraft model to calculate offset outside of mesh
        float meshSize = MainCameraUtilities.CameraTarget.GetComponent<SpacecraftController>()
            .GetMeshOffsetForMainCamera();

        // Set the main camera offset in the Hill Frame
        SetOffsetInHillFrameBSK(new[]
        {
            -9.899495f * meshSize * CelestialBodyStateUtilities.SpacecraftLocalViewScale,
            -1.41214 * meshSize * CelestialBodyStateUtilities.SpacecraftLocalViewScale, 0
        });
        //Position the main camera rig offset from the spacecraft
        UpdateInertialUnityOffset();
        cameraRigTransform.position = (MainCameraUtilities.CameraTarget.transform).position - offsetUnity;
        cameraRigTransform.up = Vector3.up;
        SetUp();
    }

    /// <summary>
    /// Calculate the Hill Frame for the camera target spacecraft
    /// </summary>
    private void CalculateTargetHillFrame() 
    {
        //Current position and velocity of the camera target in BSK coordinate frame
        double[] camTgtBodyPositionBSK = MessageList.CurrentMessage.Spacecraft[MainCameraUtilities.CameraTargetIndex]
            .Position.ToArray();
        double[] camTgtBodyVelocityBSK = MessageList.CurrentMessage.Spacecraft[MainCameraUtilities.CameraTargetIndex]
            .Velocity.ToArray();

        //Update the MainCameraUtilities.cameraTargetParentBodyIndex to what the spacecraft
        //has calculated as its primary body for this update cycle
        MainCameraUtilities.CameraTargetParentBodyIndex = MainCameraUtilities.CameraTarget
            .GetComponent<SpacecraftController>().spacecraftParentBodyIndex;

        //Current position and velocity of the camera target's parent body in BSK coordinate frame
        double[] camTgtParentPositionBSK = MessageList.CurrentMessage
            .CelestialBodies[MainCameraUtilities.CameraTargetParentBodyIndex].Position.ToArray();
        double[] camTgtParentVelocityBSK = MessageList.CurrentMessage
            .CelestialBodies[MainCameraUtilities.CameraTargetParentBodyIndex].Velocity.ToArray();

        //Calculate their relative position and velocity
        double[] rvec = OrbitVectorMath.Subtract(camTgtBodyPositionBSK, camTgtParentPositionBSK);
        double[] vvec = OrbitVectorMath.Subtract(camTgtBodyVelocityBSK, camTgtParentVelocityBSK);

        //Calculate current hill frame for camera target spacecraft
        HillFrame = OrbitVectorMath.CalculateHillFrame(rvec, vvec);
    }

    /// <summary>
    /// Set the offset of the main camera from the camera target to maintain until changed by user
    /// </summary>
    public void SetOffset()
    {
        //Set offset as the current vector of the main camera from the camera target
        offsetUnity = -cameraRigTransform.position;
        //MainCameraUtilities.cameraTarget position is zero except when in solar system view
        if (!CelestialBodyStateUtilities.ViewIsLocal)
        {
            offsetUnity += (MainCameraUtilities.CameraTarget.transform).position; //Add non-zero camera target position
        }

        //Calculate the desired offset in the current hill frame if camera target is a spacecraft or effector
        if (MainCameraUtilities.CameraTargetIsSpacecraftOrEffector)
        {
            //Rotate offset from Unity frame into the BSK frame
            double[] offsetBSK =
                OrbitVectorMath.TransformFromUnityCStoBSK(new double[]
                    {offsetUnity[0], offsetUnity[1], offsetUnity[2]});
            //Calculate the offset in the hill frame
            OffsetInHillFrameBSK = OrbitVectorMath.ApplyTransformationMatrixToVector(HillFrame, offsetBSK);
        }
    }

    /// <summary>
    /// Update the Unity frame offset to maintain the desired hill frame offset if camera target is
    /// a spacecraft or effector, otherwise return previous Unity frame offset.
    /// </summary>
    /// <returns></returns>
    private void UpdateInertialUnityOffset()
    {
        if ((MainCameraUtilities.CameraTargetIsSpacecraftOrEffector) && (!SpacecraftStateUtilities.SpacecraftMsgOnly))
        {
            //Calculate inertial offset in BSK frame using the saved hill frame offset
            double[] hillFrameTranspose = OrbitVectorMath.TransposeMatrix(HillFrame);
            double[] offsetBSKInertial =
                OrbitVectorMath.ApplyTransformationMatrixToVector(hillFrameTranspose, OffsetInHillFrameBSK);
            //Convert the offset into the Unity frame
            offsetUnity = OrbitVectorMath.ReturnVector3(OrbitVectorMath.TransformFromBSKCStoUnity(offsetBSKInertial));
        }
    }

    /// <summary>
    /// Calculate the camera rig up-direction to maintain following in the current hill frame
    /// </summary>
    public void SetUp()
    {
        cameraUp = cameraRigTransform.up;
        if (MainCameraUtilities.CameraTargetIsSpacecraftOrEffector && (!SpacecraftStateUtilities.SpacecraftMsgOnly))
        {
            //Calculate inertial offset in BSK frame using the saved hill frame offset
            double[] upVectorBSK =
                OrbitVectorMath.TransformFromUnityCStoBSK(new double[] {cameraUp[0], cameraUp[1], cameraUp[2]});
            upHillFrameBSK = OrbitVectorMath.ApplyTransformationMatrixToVector(HillFrame, upVectorBSK);
        }
    }

    /// <summary>
    /// Get the up-direction in the Unity 
    /// </summary>
    /// <returns></returns>
    private void GetUp()
    {
        // If the camera target is a spacecraft or effector and camera rig is following in the Hill Frame
        if ((MainCameraUtilities.CameraTargetIsSpacecraftOrEffector) && (!SpacecraftStateUtilities.SpacecraftMsgOnly))
        {
            double[] hillFrameTranspose = OrbitVectorMath.TransposeMatrix(HillFrame);
            //Update the up direction in the BSK coordinate frame for the latest hill frame
            double[] upVectorBSKInertial =
                OrbitVectorMath.ApplyTransformationMatrixToVector(hillFrameTranspose, upHillFrameBSK);
            //Convert the up direction to the Unity frame
            cameraUp = OrbitVectorMath.ReturnVector3(OrbitVectorMath.TransformFromBSKCStoUnity(upVectorBSKInertial));
        }
    }

    /// <summary>
    /// Set the offset of the camera rig from the camera target in the spacecraft camera target's Hill Frame
    /// </summary>
    /// <param name="newOffset">Hill Frame offset to apply</param>
    private void SetOffsetInHillFrameBSK(double[] newOffset)
    {
        OffsetInHillFrameBSK = newOffset;
    }
}