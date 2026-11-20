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
using UnityEngine;
using VizProtobufferMessage;
/// <summary>
/// Static class providing methods and object references for spacecraft instantiated for the current scenario.
/// </summary>
public static class SpacecraftStateUtilities{
    public static List<GameObject> SpacecraftList;
    public static List<GameObject> ParentSpacecraftList;
    public static List<GameObject> EffectorList;
    public static List<GameObject> SpacecraftOrbitLinesList = new List<GameObject>();
    public static Dictionary<string, List<int>> ParentAndEffectorDictionary;
	
    private static List<string> instrumentsList;
    private static List<string> actuatorsList;
	
    public static double[,] ChiefPositions;
    public static double[,] ChiefDCMs;
    private static int relativeTruePathChangeCount = -2;
    private static int visibleHistoryUpdateCount = -2;

    public static float DefaultLocalViewSpacecraftScale = 5f;
    public static float DefaultHelioViewSpacecraftScale = 5f;

    private static float spacecraftLocalSpacecraftOrbitLineWidth = 0.0625f;
    private static float planetLocalSpacecraftOrbitLineWidth = 0.25f;
    private static float helioViewSpacecraftOrbitLineWidth = 1f; //2f

    private static float spacecraftLocalGroundTrackLineWidth = 100f;
    private static float planetLocalGroundTrackLineWidth = 1f;
    private static float helioViewGroundTrackLineWidth = 0.1f;
	
    public static bool SpacecraftMsgOnly; //True if the only object in the current sim is a spacecraft

    public static Dictionary<string, string[]> SpacecraftModels = new Dictionary<string, string[]>()
    {					
        // key						[0] modelPath		[1] Type (R = Resource, F = Filepath, A = Addressable)
        {"bsksat", new[] {"Models/BSKSAT_model",          "R"}},
        {"6usat", new[]  {"Models/CubeSAT_6U",            "R"}},
        {"3usat", new[]  {"Models/CubeSAT_3U",            "R"}},
        {"TRI", new[] {"Models/Triangle",                 "R"}},
        // Scarlet Alpha demo: the SAR satellites (Basilisk modelDictionaryKey "sentinel1SAR")
        {"sentinel1sar", new[] {"Models/Sentinel1SAR/sentinel1_sar", "R"}},
        {"iceye", new[] {"Models/ICEYE/ICEYE_vizard", "R"}}  // ICEYE.obj in metres, centred, on the body axes
    };
    public static GameObject GetSpacecraftObject(string spacecraftName){
        foreach(GameObject sc in SpacecraftList){
            if (sc.name == spacecraftName){
                return sc;
            }
        }
        return null;
    }
	
    public static GameObject GetSpacecraftObject(int scIndex)
    {
        if ((scIndex >= 0) && (scIndex < SpacecraftList.Count))
        {
            return SpacecraftList[scIndex];
        }
        return null;
    }

    public static int GetSpacecraftIndex(string spacecraftName){
        for (int i=0; i < SpacecraftList.Count; i++){
            if (SpacecraftList[i].name == spacecraftName){
                return i;
            }
        }
        VizardGUISettings.UpdateErrorMessages("Unable to match spacecraft name of "+spacecraftName+" in messages", true);
        return -1;
    }



    public static double[] GetAbsSpacecraftPositionUnityCS(int spacecraftIndex)
    {
        VizMessage.Types.Spacecraft spacecraft = MessageList.CurrentMessage.Spacecraft[spacecraftIndex];
        if (spacecraft == null)
        {
            return new double[]{0,0,0};
        }
		
        return OrbitVectorMath.TransformFromBSKCStoUnity(new[]{spacecraft.Position[0], spacecraft.Position[1],
            spacecraft.Position[2]});
    }
	
    public static double[] GetAbsSpacecraftVelocityUnityCS(int spacecraftIndex)
    {
        VizMessage.Types.Spacecraft spacecraft = MessageList.CurrentMessage.Spacecraft[spacecraftIndex];
        if (spacecraft == null)
        {
            return new double[]{0,0,0};
        }
		
        return OrbitVectorMath.TransformFromBSKCStoUnity(new[]{spacecraft.Velocity[0], spacecraft.Velocity[1],
            spacecraft.Velocity[2]});
    }

    public static Quaternion GetSpacecraftOrientationUnityCS(int spacecraftIndex)
    {
        VizMessage.Types.Spacecraft spacecraft = MessageList.CurrentMessage.Spacecraft[spacecraftIndex];
        if (spacecraft == null)
        {
            return Quaternion.identity;
        }
        double[] spacecraftMRPBSK = {spacecraft.Rotation[0], spacecraft.Rotation[1], spacecraft.Rotation[2]};

        //Send it off to be converted to a quaternion and into the Unity left handed CS
        return OrbitVectorMath.ConvertRightHandedMRPtoLeftHandedQuaternion(spacecraftMRPBSK);
    }

	
    public static void MoveEntireGameObjectToLayer(Transform bodyToMoveTransform, int layer){
        bodyToMoveTransform.gameObject.layer = layer;
        foreach(Transform child in bodyToMoveTransform)
            MoveEntireGameObjectToLayer(child, layer);
    }

    public static Bounds CalculateModelBounds(GameObject model){
        //Calculate the center and extents of the imported model. 
        //https://answers.unity.com/questions/17968/finding-the-bounds-of-a-grouped-model.html?_ga=2.201535193.958722128.1569527139-981915272.1544738346
        //https://forum.unity.com/threads/calculating-a-bound-of-a-grouped-model.101121/
        Renderer[] rr = model.transform.GetComponentsInChildren<Renderer>();
        Bounds completeBounds = rr[0].bounds;
        foreach(Renderer r in rr){
            completeBounds.Encapsulate(r.bounds);
        }
	
        return completeBounds;
    }

    public static void ResetOrbitLines(){
        //Use this to reset the list of position points when messages get compressed
        foreach (GameObject sc in ParentSpacecraftList){
            sc.GetComponent<SpacecraftController>().orbitLine.GetComponent<OsculatingOrbitLine>().truePathOrbitLine.InitializeTruePathLine(sc, sc.GetComponent<SpacecraftController>().spacecraftIndex );
        }
    }
	
    public static void UpdateChiefSpacecraft(int newChiefIndex=-1, bool parentBodyChange=false)
    {
        if ((VizardGUISettings.RelativeTruePathChangeCount != relativeTruePathChangeCount)||(MessageList.VisibleHistoryUpdateCount!= visibleHistoryUpdateCount)||(parentBodyChange))
        {
            VizardGUISettings.ChiefSpacecraftIndex = newChiefIndex;
            int chiefSCParentBodyIndex = SpacecraftList[newChiefIndex].GetComponent<SpacecraftController>().spacecraftParentBodyIndex;
            relativeTruePathChangeCount = VizardGUISettings.RelativeTruePathChangeCount;
            visibleHistoryUpdateCount = MessageList.VisibleHistoryUpdateCount;
			
            ChiefPositions = MessageList.GetPositionHistoryBSK(true, newChiefIndex);
            double[,] chiefVelocities = MessageList.GetVelocityHistoryBSK(true, newChiefIndex);
            ChiefDCMs = new double[chiefVelocities.GetLength(0), 9];
			
            double[,] parentPositions = MessageList.GetPositionHistoryBSK(false, chiefSCParentBodyIndex);
            double[,] parentVelocities = MessageList.GetVelocityHistoryBSK(false, chiefSCParentBodyIndex);

            double[] relativePosition = {0,0,0};
            double[] relativeVelocity = {0, 0, 0};
            for (int i = 0; i < ChiefPositions.GetLength(0); i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    relativePosition[j] = ChiefPositions[i, j] - parentPositions[i, j];
                    relativeVelocity[j] = chiefVelocities[i, j] - parentVelocities[i, j];
                }

                double[] DCM;
                if (VizardGUISettings.SpacecraftRelativeOrbitMode == 2)
                {
                    DCM = OrbitVectorMath.CalculateVelocityFrame(relativePosition, relativeVelocity);
                }
                else
                {
                    DCM = OrbitVectorMath.CalculateHillFrame(relativePosition, relativeVelocity);
                }
                for (int j = 0; j < 9; j++)
                {
                    ChiefDCMs[i, j] = DCM[j];
                }
            }
        }
    }
	
    public static void AppendChiefSpacecraftData(int currentMsgCount)
    {
        int oldArrayLength = ChiefPositions.GetLength(0);
        if (oldArrayLength < currentMsgCount)
        {
            double[,] newChiefPositions = new double[currentMsgCount, 3];
            double[,] newChiefDCMs = new double[currentMsgCount, 9];
			
            for (int i = 0; i < oldArrayLength; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    newChiefPositions[i, j] = ChiefPositions[i, j];
                }
				
                for (int j = 0; j < 9; j++)
                {
                    newChiefDCMs[i, j] = ChiefDCMs[i, j];
                }
            }
			
            int chiefSCParentBodyIndex = SpacecraftList[VizardGUISettings.ChiefSpacecraftIndex]
                .GetComponent<SpacecraftController>().spacecraftParentBodyIndex;

            for (int k = oldArrayLength; k < currentMsgCount; k++)
            {
                VizMessage message = MessageList.GetMessageAtIndex(k);
                for (int i = 0; i < 3; i++)
                {
                    newChiefPositions[k, i] =
                        message.Spacecraft[VizardGUISettings.ChiefSpacecraftIndex].Position[i];
                }
				
                double[] relativePosition = new double[3];
                double[] relativeVelocity = new double[3];

                for (int j = 0; j < 3; j++)
                {
                    relativePosition[j] = newChiefPositions[k, j] -
                                          message.CelestialBodies[chiefSCParentBodyIndex].Position[j];
                    relativeVelocity[j] = message.Spacecraft[VizardGUISettings.ChiefSpacecraftIndex].Velocity[j] -
                                          message.CelestialBodies[chiefSCParentBodyIndex].Velocity[j];
                }

                double[] chiefDCM;
                if (VizardGUISettings.SpacecraftRelativeOrbitMode == 1) //Calc Hill Frame
                {
                    chiefDCM = OrbitVectorMath.CalculateHillFrame(relativePosition, relativeVelocity);
                }
                else //Velocity Frame
                {
                    chiefDCM = OrbitVectorMath.CalculateVelocityFrame(relativePosition, relativeVelocity);
                }

                for (int j = 0; j < 9; j++)
                {
                    newChiefDCMs[k, j] = chiefDCM[j];
                }
            }

            ChiefDCMs = newChiefDCMs;
            ChiefPositions = newChiefPositions;
        }
    }

    public static Color GetOscOrbitColor(int scIndex)
    {
        Color lineColor= Color.black;
        int colorCount = MessageList.CurrentMessage.Spacecraft[scIndex].OscOrbitLineColor.Count;
        if (colorCount >= 3)
        {
            if (MessageList.CurrentMessage.Spacecraft[scIndex].OscOrbitLineColor[0] >= 0)
            {
                lineColor = new Color(
                    MessageList.CurrentMessage.Spacecraft[scIndex].OscOrbitLineColor[0] / 255f,
                    MessageList.CurrentMessage.Spacecraft[scIndex].OscOrbitLineColor[1] / 255f,
                    MessageList.CurrentMessage.Spacecraft[scIndex].OscOrbitLineColor[2] / 255f, 1f);
                if (colorCount >= 4)
                {
                    lineColor.a = MessageList.CurrentMessage.Spacecraft[scIndex].OscOrbitLineColor[3] / 255f;
                }
				
            }
        }
        return lineColor;
    }

    public static float GetMeshDimension(int scIndex)
    {
        GameObject sc = GetSpacecraftObject(scIndex);
        if (sc != null)
        {
            return GetSpacecraftObject(scIndex).GetComponent<SpacecraftController>().meshDimension;
        }
        else
        {
            return -1f;
        }
    }

    public static Color GetTruePathColorFromMessage(VizProtobufferMessage.VizMessage.Types.Spacecraft scMsg, Color currentColor)
    {
        Color newColor = currentColor;
        int colorFieldCount = scMsg.TrueTrajectoryLineColor.Count;
        if (colorFieldCount >= 3)
        {
            newColor = new Color(
                scMsg.TrueTrajectoryLineColor[0] / 255f,
                scMsg.TrueTrajectoryLineColor[1] / 255f,
                scMsg.TrueTrajectoryLineColor[2] / 255f, 1f
            );
            if (colorFieldCount >= 4)
            {
                newColor.a = scMsg.TrueTrajectoryLineColor[3] / 255f;
            }
        }
        return newColor;
    }
	
    public static Color GetGroundTrackColorFromMessage(VizProtobufferMessage.VizMessage.Types.Spacecraft scMsg, Color currentColor)
    {
        Color newColor = currentColor;
        int colorFieldCount = scMsg.GroundTrackLineColor.Count;
        if (colorFieldCount >= 3)
        {
            newColor = new Color(
                scMsg.GroundTrackLineColor[0] / 255f,
                scMsg.GroundTrackLineColor[1] / 255f,
                scMsg.GroundTrackLineColor[2] / 255f, 1f
            );
            if (colorFieldCount >= 4)
            {
                newColor.a = scMsg.GroundTrackLineColor[3] / 255f;
            }
        }

        return newColor;
    }
	
    public static List<string> ActuatorsList //Move this to another script
    {
        get
        {
            if (actuatorsList == null)
            {
                actuatorsList = new List<string>();
                bool hasRW = false;
                bool hasThrusters = false;

                foreach (VizProtobufferMessage.VizMessage.Types.Spacecraft spacecraft in MessageList.FirstMessage.Spacecraft)
                {
                    if (spacecraft.ReactionWheels.Count > 0)
                    {
                        hasRW = true;
                    }

                    if (spacecraft.Thrusters.Count > 0)
                    {
                        hasThrusters = true;
                    }

                    if ((hasRW) && (hasThrusters))
                    {
                        break;
                    }
                }

                if (hasRW)
                {
                    actuatorsList.Add("ReactionWheel");
                }

                if (hasThrusters)
                {
                    actuatorsList.Add("Thruster");
                }
            }

            return actuatorsList;
        }
    }

    public static List<string> InstrumentsList //Move this to another script
    {
        get
        {
            if (instrumentsList == null)
            {
                bool hasCSS = false;
                bool hasGS = false;
                bool hasTx = false;
                bool hasSD = false;
                instrumentsList = new List<string>();
                foreach (VizProtobufferMessage.VizMessage.Types.Spacecraft spacecraft in MessageList.FirstMessage.Spacecraft)
                {
                    if (spacecraft.CSS.Count > 0)
                    {
                        hasCSS = true;
                    }

                    if (spacecraft.GenericSensors.Count > 0)
                    {
                        hasGS = true;
                    }

                    if (spacecraft.Transceivers.Count > 0)
                    {
                        hasTx = true;
                    }

                    if (spacecraft.StorageDevices.Count > 0)
                    {
                        hasSD = true;
                    }

                    if ((hasCSS) && (hasGS) && (hasTx) && (hasSD))
                    {
                        break;
                    }
                }

                if (hasCSS)
                {
                    instrumentsList.Add("CSS");
                }

                if (hasGS)
                {
                    instrumentsList.Add("GenericSensor");
                }

                if (hasTx)
                {
                    instrumentsList.Add("Transceiver");
                }

                if (hasSD)
                {
                    instrumentsList.Add("GenericStorage");
                }
            }

            return instrumentsList;
        }
    }

    public static void UpdateSpacecraftCSVisibility()
    {
        foreach (GameObject sc in ParentSpacecraftList)
        {
            sc.GetComponent<SpacecraftController>().inertialCoordinateAxes.SetActive(VizardGUISettings.AllSpacecraftCSOn);

            if (MainCameraUtilities.CameraTarget.CompareTag("Spacecraft"))
            {
                if (sc.name == MainCameraUtilities.CameraTarget.name)
                {
                    sc.GetComponent<SpacecraftController>().inertialCoordinateAxes.SetActive(VizardGUISettings.CameraTargetCSOn);
                }
            }
        }
    }
    
    public static void UpdateEffectorCSVisibility()
    {
        bool mainCameraTargetIsEffector = MainCameraUtilities.CameraTarget.CompareTag("Spacecraft") &&
                                          MainCameraUtilities.CameraTarget.GetComponent<SpacecraftController>()
                                              .isEffector;
        foreach (GameObject sc in EffectorList) {
            sc.transform.GetChild (2).gameObject.SetActive (VizardGUISettings.AllEffectorCSOn);
            if ((sc.name == MainCameraUtilities.CameraTarget.name)&&(mainCameraTargetIsEffector)) 
            {
                sc.transform.GetChild (2).gameObject.SetActive (VizardGUISettings.CameraTargetCSOn);
            }
        }
    }
    
    public static void ResetSpacecraftStateUtilities()
    {
        SpacecraftList = new List<GameObject>();
        ParentSpacecraftList = new List<GameObject>();
        EffectorList = new List<GameObject>();
        SpacecraftOrbitLinesList = new List<GameObject>();
        ParentAndEffectorDictionary = new Dictionary<string, List<int>>();
        instrumentsList = null;
        actuatorsList = null;
        ChiefPositions = new double[,]{};
        ChiefDCMs = new double[,]{};
        relativeTruePathChangeCount = -2;
        visibleHistoryUpdateCount = -2;

        DefaultLocalViewSpacecraftScale = 5f;
        DefaultHelioViewSpacecraftScale = 5f;

        SpacecraftMsgOnly = false;
    }

    public static void UpdateSpacecraftOrbitLineWidth()
    {
        float newOrbitLineWidth = GetCurrentSpacecraftOrbitLineWidth();
        float newGroundTrackLineWidth = GetCurrentGroundTrackLineWidth();
        foreach (GameObject line in SpacecraftOrbitLinesList)
        {
            line.GetComponent<OsculatingOrbitLinePlotter>().UpdateLineRendererLineThickness(newOrbitLineWidth, true);
            line.GetComponentInChildren<GroundTrackOsculating>().UpdateMarkerAndLineRendererLineThickness(newGroundTrackLineWidth);
            line.GetComponentInChildren<GroundTrackTruePath>().UpdateMarkerAndLineRendererLineThickness(newGroundTrackLineWidth);
        }
    }

    public static float GetCurrentSpacecraftOrbitLineWidth()
    {
        float newWidth = GetCurrentSpacecraftOrbitLineConstant();
        newWidth *= (float) PersistentUserSettings.persistentSettingsFromLastSave.SpacecraftOrbitLineWidth;
        return newWidth;
    }

    public static float GetCurrentSpacecraftOrbitLineConstant()
    {
        float constant = helioViewSpacecraftOrbitLineWidth;
	    
        if (CelestialBodyStateUtilities.ViewIsLocal)
        {
            constant = CelestialBodyStateUtilities.ViewIsSpacecraftLocal? (spacecraftLocalSpacecraftOrbitLineWidth): planetLocalSpacecraftOrbitLineWidth; 
        }

        return constant;
    }

    public static float GetCurrentGroundTrackLineWidth()
    {
        float newWidth = helioViewGroundTrackLineWidth;
	    
        if (CelestialBodyStateUtilities.ViewIsLocal)
        {
            newWidth = CelestialBodyStateUtilities.ViewIsSpacecraftLocal? (spacecraftLocalGroundTrackLineWidth): planetLocalGroundTrackLineWidth; 
        }

        return newWidth;
    }
    
    public static void UpdateThrusterGeometry()
    {
        foreach(GameObject sc in SpacecraftList){
            sc.GetComponent<SpacecraftController>().UpdateThrusterGeometry();
        }
    }
}