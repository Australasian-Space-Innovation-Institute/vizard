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
using System.Linq;
using UnityEngine;
using VizProtobufferMessage;
/// <summary>
/// Calculates the instantaneous osculating orbit
/// ground track in the fixed frame
/// <remarks>Only works for spherical bodies (not irregular bodies, like asteroids</remarks>
/// </summary>
public class GroundTrackOsculating : MonoBehaviour
{
	public GameObject markerSphere;
    private GameObject parentBody;
    private OsculatingOrbitLine oscOrbitLine;
    public int parentBodyIndex;
    private int spacecraftIndex;
    private double parentBodyMu;
    private LineRenderer groundTrackLine;
    private readonly float bodyOffsetFactor = 1.001f;
    private readonly float helioMarkerScale = 0.02f;
    private readonly float planetMarkerScale = 0.01f;
    private readonly float scMarkerScale = 0.005f;
    private Vector3[] groundTrackPoints= new Vector3[1];
    
    private double[] OE = new double[10]; //Orbit elements returned from orbitalMotion.rv2elem
    private Vector3[] oscOrbitPoints = new Vector3[1];
    private double[] timesOfFlight = new double[1];
    private double currentTimeOfFlight;
    private bool currentTAWithinDegreeRange;

    //public double currentHAtoRemember;

    private int segmentsPer360 = 180;
    private int pastOrbitDegreeRange=-180;
    private int futureOrbitDegreeRange=180;
    private int fullRange;
    private int segmentCount = 180;
    private double TAincrementRad;
    
    private double[] lastDCMdiff={1,0,0,0,1,0,0,0,1};
    private double lastSimTimeElapsed;
    private double lastTimeDiff;
    private int lastIndex;
    private bool parentBodyRotates;
    private const double Deg2Rad = Math.PI / 180.0;

    private Vector3 currentTAPos;
    private int currentTAIndex;
    private bool forceGroundTrackUpdate;
    

    void Awake()
    {
        groundTrackLine = GetComponent<LineRenderer>();
    }
    
    public void InitializeGroundTrackLine(int scParentBodyIndex, int scIndex, OsculatingOrbitLine ooL)
    {
	    parentBodyIndex = scParentBodyIndex;
	    spacecraftIndex = scIndex;
	    oscOrbitLine = ooL;
	    
		//UpdateOrbitLineSegmentCountAndGroundTrackRange();
	    SetParentBody();
	    parentBodyRotates = CheckParentBodyRotates();
	    
	    ToggleOsculatingGroundTrackLine(VizardGUISettings.OsculatingGroundTrackOn);
    }

    void FixedUpdate()
    {
	    if (groundTrackLine.enabled)
	    {
		    if (segmentsPer360 != PersistentUserSettings.persistentSettingsFromLastSave.OrbitLineSegments)
		    {
			    UpdateOrbitLineSegmentCountAndGroundTrackRange();
			    forceGroundTrackUpdate = true;
		    }
		    if ((lastIndex != MessageList.CurrentIndex)||(forceGroundTrackUpdate)||(MessageList.PlaybackPaused))
		    {
			    UpdateGroundTrack();
			    forceGroundTrackUpdate = false;
		    }
	    }
    }

    private void UpdateGroundTrack()
    {
	    lastIndex = MessageList.CurrentIndex;
	    if (oscOrbitLine.parentBodyIndex != parentBodyIndex)
	    {
		    parentBodyIndex = oscOrbitLine.parentBodyIndex;
		    SetParentBody();
	    }
	    transform.position = parentBody.transform.position;
	    transform.localScale = parentBody.transform.localScale;

	    UpdateColor();

	    OE = oscOrbitLine.GetOrbitalElements();
	    CalculateOsculatingOrbitPointsAndTimesOfFlight();
	    TransformOsculatingOrbitToGroundTrack();
	    PlotGroundTrack();
    }
    
    private void SetParentBody()
    {
	    parentBody = CelestialBodyStateUtilities.GetCelestialBodyObject(parentBodyIndex);
	    parentBodyMu = CelestialBodyStateUtilities.GetMu(parentBody.name);
    }

    private bool CheckParentBodyRotates()
    {
	    //Spot check some rotations
	    //Scarlet demo app: a live run's scene can be set up before its second
	    //message has arrived (a reset mid-stream); with too few messages to
	    //compare, assume the body rotates (Earth does) rather than throw, which
	    //aborted the whole scene setup.
	    VizMessage firstMessage = MessageList.FirstMessage;
	    VizMessage secondMessage = MessageList.GetMessageAtIndex(1);
	    int randomMessage = UnityEngine.Random.Range(MessageList.LastMessageIndexOfPlottedMessages/2, MessageList.LastMessageIndexOfPlottedMessages);
	    VizMessage spotCheckMessage = MessageList.GetMessageAtIndex(randomMessage);
	    if (firstMessage == null || secondMessage == null || spotCheckMessage == null)
	    {
		    return true;
	    }
	    double[] firstMessageRotation = firstMessage.CelestialBodies[parentBodyIndex].Rotation.ToArray();
	    double[] secondMessageRotation = secondMessage.CelestialBodies[parentBodyIndex].Rotation.ToArray();
	    double[] spotCheckMessageRotation = spotCheckMessage.CelestialBodies[parentBodyIndex].Rotation.ToArray();

	    for (int i = 0; i < 9; i++)
	    {
		    if (firstMessageRotation[i] - secondMessageRotation[i]>OrbitVectorMath.EPS)
		    {
			    return true;
		    }
		    if (firstMessageRotation[i] - spotCheckMessageRotation[i] > OrbitVectorMath.EPS)
		    {
			    return true;
		    }
		    if (secondMessageRotation[i] - spotCheckMessageRotation[i] > OrbitVectorMath.EPS)
		    {
			    return true;
		    }
	    }
	    return false;
    }

    private void UpdateColor()
    {
	    Color newColor = VizardGUISettings.CreateColorFromIntArray(MessageList.CurrentMessage
		    .Spacecraft[spacecraftIndex].GroundTrackLineColor.ToArray());
	    groundTrackLine.startColor = newColor;
	    groundTrackLine.endColor = newColor;
    }


    private void CalculateOsculatingOrbitPointsAndTimesOfFlight()
    {
	    if (oscOrbitPoints.Length != segmentCount + 1)
	    {
		    oscOrbitPoints = new Vector3[segmentCount + 1];
		    if (parentBodyRotates)
		    {
			    timesOfFlight = new double[segmentCount + 1];
		    }
	    }

	    //Using the OE, calculate position array and the times of flight
	    double currentTA = OE[5];
	    double startTA = currentTA + pastOrbitDegreeRange * Deg2Rad;
	    double endTA = currentTA + futureOrbitDegreeRange * Deg2Rad;
	    double eccentricity = OE[1];
	    double a = OE[0];
	    currentTAWithinDegreeRange = (currentTA >= startTA) && (currentTA <= endTA);
	    //Debug.Log($"pastOrbitDegreeRange: {pastOrbitDegreeRange},futureOrbitDegreeRange: {futureOrbitDegreeRange} deg2rad: {deg2rad}, currentTA: {currentTA}, startTA: {startTA}, endTA: {endTA}");

	    if ((1 / a) > 0.0)
	    {
		    //Closed orbit
		    double aCubedOverMuSqrtd = Math.Sqrt(a * a * a / parentBodyMu);
		    double eccentricAnomalyT0 = OrbitalMotion.CalculateEccentricAnomaly(currentTA, eccentricity);
		    double meanAnomalyT0 = eccentricAnomalyT0 - eccentricity * Math.Sin(eccentricAnomalyT0);
		    double totalPeriod = 2 * Math.PI * aCubedOverMuSqrtd;
		    
		    double cta = startTA;
		    for (int i = 0; i <= segmentCount; i++)
		    {

			    //Calculate orbit position relative to center of parent body
			    double[] positionForCurrentTABSK =
				    OrbitVectorMath.ScaleVector(OrbitalMotion.elem2pos(cta, OE), 1000.0f);
			    //Transform into BSK CS and store as Vector3
			    oscOrbitPoints[i] = new Vector3((float) positionForCurrentTABSK[0],
				    (float) positionForCurrentTABSK[1], (float) positionForCurrentTABSK[2]);
			    if ((currentTAWithinDegreeRange)&&(Math.Abs(cta - currentTA) < 0.001))
			    {
				    currentTAIndex = i;
			    }

			    if (parentBodyRotates)
			    {
				    //Calculate time of flight (this is set up to work for multiple orbits)
				    timesOfFlight[i]=CalculateTimeOfFlightClosedOrbit(cta, eccentricity,  aCubedOverMuSqrtd, meanAnomalyT0, totalPeriod);
			    }

			    cta += TAincrementRad;
			    if (cta > endTA)
			    {
				    cta = endTA;
			    }
		    }
		    if (!currentTAWithinDegreeRange)
		    {
			    currentTimeOfFlight = CalculateTimeOfFlightClosedOrbit(currentTA, eccentricity,
				    aCubedOverMuSqrtd, meanAnomalyT0, totalPeriod);
		    }
		    else
		    {
			    currentTimeOfFlight = timesOfFlight[currentTAIndex];
		    }
	    }
	    else
	    {
		    //Orbit is parabolic or hyperbolic and true anomaly range is limited
		    double aCubedOverMuSqrtd = Math.Sqrt(-a * a * a / parentBodyMu);
		    double minTALimit = 0.99 * (-Math.PI + Math.Acos((1.0 / eccentricity))); //radians
		    double maxTALimit = 0.99 * (Math.PI - Math.Acos(1.0 / eccentricity));
		    if (minTALimit > startTA)
		    {
			    startTA = minTALimit;
		    }
			//Calculate segmentCount
			bool endTAGreaterThanTALimit = false;
		    if (maxTALimit < endTA)
		    {
			    endTA = maxTALimit;
			    endTAGreaterThanTALimit = true;
		    }

		    
		    double totalRange = endTA - startTA;
		    double hyperTAInc = totalRange / segmentCount;
		    bool currentTAInsideRange = ((startTA < currentTA) && (endTA > currentTA));

		    double cta = startTA;
		    bool setCurrentTAIndex = true;
		    for (int i = 0; i <= segmentCount; i++)
		    {
			    if ((setCurrentTAIndex)&& (currentTAInsideRange)&&(cta>=currentTA))
			    {
				    currentTAIndex = i;
				    cta = currentTA;
				    setCurrentTAIndex = false;
			    }
			    if ((endTAGreaterThanTALimit) && (i == segmentCount))
			    {
				    cta = endTA;
			    }
			    double[] positionForCurrentTABSK = OrbitVectorMath.ScaleVector(OrbitalMotion.elem2pos(cta, OE), 1000);
			    oscOrbitPoints[i] = new Vector3((float) positionForCurrentTABSK[0],
				    (float) positionForCurrentTABSK[1], (float) positionForCurrentTABSK[2]);
			    if (parentBodyRotates)
			    {
				    timesOfFlight[i]=CalculateTimeOfFlightOpenOrbit(cta, eccentricity, aCubedOverMuSqrtd);
			    }
			    cta += hyperTAInc;
		    }
		    currentTimeOfFlight = !currentTAWithinDegreeRange ? CalculateTimeOfFlightOpenOrbit(currentTA, eccentricity, aCubedOverMuSqrtd) : timesOfFlight[currentTAIndex];
	    }
    }

    private double CalculateTimeOfFlightOpenOrbit(double cta, double eccentricity, double aCubedOverMuSqrtd)
    {
	    double H = OrbitalMotion.CalculateHyperbolicAnomaly(cta, eccentricity);
	    return aCubedOverMuSqrtd * (eccentricity * Math.Sinh(H) - H);
    }

    private double CalculateTimeOfFlightClosedOrbit(double cta, double eccentricity, double aCubedOverMuSqrtd,
	    double meanAnomalyT0, double totalPeriod)
    {
	    double currentTACalc = cta;
	    int addLoop = 0;
	    int currentSign = 1 * Math.Sign(cta);
	    while (Math.Abs(currentTACalc) > 2.0 * Math.PI)
	    {
		    currentTACalc -= currentSign * 2.0 * Math.PI;
		    addLoop += currentSign;
	    }

	    double eccentricAnomaly = OrbitalMotion
		    .CalculateEccentricAnomaly(currentTACalc, eccentricity);
	    double meanAnomaly = eccentricAnomaly - eccentricity * Math.Sin(eccentricAnomaly);
	    return aCubedOverMuSqrtd * (meanAnomaly - meanAnomalyT0) + addLoop * totalPeriod;
    }


    private void TransformOsculatingOrbitToGroundTrack()
    {
	    if (groundTrackPoints.Length != oscOrbitPoints.Length)
	    {
		    groundTrackPoints = new Vector3[oscOrbitPoints.Length];
	    }

	    if (parentBodyRotates)
	    {
		    double[] dcmDiff = {1, 0, 0, 0, 1, 0, 0, 0, 1};
		    double timeDiff = lastSimTimeElapsed;
		    double[] currentParentDCM =
			    MessageList.CurrentMessage.CelestialBodies[parentBodyIndex].Rotation.ToArray();
		    double[] currentParentDCMTranspose = OrbitVectorMath.TransposeMatrix(currentParentDCM);
		    
		    if ((MessageList.InBufferLoad)||(MessageList.PlaybackPaused)||(lastIndex+1>=MessageList.LastMessageIndexOfPlottedMessages)||(lastIndex+1>=MessageList.TimestepsTotal)) //If we are in between rotation updates, don't recalculate everything
		    {
			    dcmDiff = lastDCMdiff;
			    timeDiff = lastTimeDiff;
		    }
		    else
		    {
			    bool rotationUpdateFound = false;
			    int messageIndexToTry = lastIndex + 1;
			    while (!rotationUpdateFound) //Not all sims update celestial body rotations at every time step
			    {
				    VizMessage nextMessage = MessageList.GetMessageAtIndex(messageIndexToTry);
				    if (nextMessage != null)
				    {
					    double[] nextParentDCM = nextMessage.CelestialBodies[parentBodyIndex].Rotation.ToArray();
					    double nextTime = nextMessage.CurrentTime.SimTimeElapsed;
					    
					    for (int i = 0; i < 9; i++)
					    {
						    if (Math.Abs(nextParentDCM[i] - currentParentDCM[i]) > OrbitVectorMath.EPS)
						    {
							    rotationUpdateFound = true;
							    break;
						    }
					    }

					    if (rotationUpdateFound)
					    {
						    timeDiff = nextTime - lastSimTimeElapsed;
						    dcmDiff = OrbitVectorMath.Dot3x3Matrix(nextParentDCM,
							    OrbitVectorMath.TransposeMatrix(currentParentDCM));
						    lastDCMdiff = dcmDiff;
						    lastTimeDiff = timeDiff;
						    lastSimTimeElapsed = nextMessage.CurrentTime.SimTimeElapsed;
					    }
					    else
					    {
						    messageIndexToTry++;
						    //check we haven't reached end of file or end of buffer
						    if ((messageIndexToTry>MessageList.LastMessageIndexOfPlottedMessages)||(messageIndexToTry >= MessageList.TimestepsTotal))
						    { 
							    rotationUpdateFound = true;
							    dcmDiff = lastDCMdiff;
							    timeDiff = lastTimeDiff;
							    
						    }
					    }
				    }
			    }
		    }

		    timeDiff /= 1E9; //time in seconds

		    double[] prvPDiff = OrbitalMotion.DCM2PRV(dcmDiff);
		    double[] omegaPfix2 = OrbitVectorMath.ScaleVector(prvPDiff, (1 / timeDiff));
		    for (int i = 0; i < groundTrackPoints.Length; i++)
		    {
			    var PRVDiff = OrbitVectorMath.ScaleVector(omegaPfix2, timesOfFlight[i] - currentTimeOfFlight);
			    var DCMDiffFuture = OrbitalMotion.PRV2DCM(PRVDiff);
			    var DCMFuture = OrbitVectorMath.Dot3x3Matrix(DCMDiffFuture, currentParentDCM);
			    var fixedFramePositionBSK = new double[] {oscOrbitPoints[i].x, oscOrbitPoints[i].y, oscOrbitPoints[i].z};
			    fixedFramePositionBSK =
				    OrbitVectorMath.ApplyTransformationMatrixToVector(DCMFuture, fixedFramePositionBSK);
			    fixedFramePositionBSK =
				    OrbitVectorMath.ApplyTransformationMatrixToVector(currentParentDCMTranspose, fixedFramePositionBSK);
			    groundTrackPoints[i] = OrbitVectorMath.ReturnVector3(OrbitVectorMath.TransformFromBSKCStoUnity(fixedFramePositionBSK));
		    }
	    }
	    else
	    {
		    for (int i = 0; i < oscOrbitPoints.Length; i++)
		    {
			    groundTrackPoints[i] = OrbitVectorMath.TransformFromBSKCStoUnity(oscOrbitPoints[i]);
		    }
	    }

	    currentTAPos = currentTAWithinDegreeRange ? groundTrackPoints[currentTAIndex] : Vector3.zero;

    }
    

    private void PlotGroundTrack()
    {
	    currentTAPos = currentTAPos.normalized * bodyOffsetFactor;
	    markerSphere.transform.localPosition = currentTAPos;
	    
        for (int i = 0; i < groundTrackPoints.Length; i++)
        {
            groundTrackPoints[i] = bodyOffsetFactor*groundTrackPoints[i].normalized;
        }
		groundTrackLine.positionCount = groundTrackPoints.Length;
		groundTrackLine.SetPositions(groundTrackPoints);
    }

    public void UpdateOrbitLineSegmentCountAndGroundTrackRange()
    {
	    segmentsPer360 = PersistentUserSettings.persistentSettingsFromLastSave.OrbitLineSegments;
	    if (PersistentUserSettings.persistentSettingsFromLastSave.OsculatingGroundTrackRange.Count >= 2)
	    {
		    pastOrbitDegreeRange = PersistentUserSettings.persistentSettingsFromLastSave.OsculatingGroundTrackRange[0];
		    futureOrbitDegreeRange = PersistentUserSettings.persistentSettingsFromLastSave.OsculatingGroundTrackRange[1];
	    }
	    else
	    {
		    pastOrbitDegreeRange = -PersistentUserSettings.persistentSettingsFromLastSave.RelativeOrbitRange;
		    futureOrbitDegreeRange = PersistentUserSettings.persistentSettingsFromLastSave.RelativeOrbitRange;
	    }

	    if ((pastOrbitDegreeRange>=0)||(futureOrbitDegreeRange<=0))
	    {
		    
		    fullRange = futureOrbitDegreeRange - pastOrbitDegreeRange;
		    segmentCount = Mathf.CeilToInt(fullRange / 360.0f * segmentsPer360);
		    TAincrementRad = fullRange * Deg2Rad / segmentCount;
		    Debug.Log($"Hey! {fullRange}, {segmentCount}, {TAincrementRad}");
	    }
	    else
	    {
		    int pastAbsoluteDegreeRange = Mathf.Abs(pastOrbitDegreeRange);
		    int pastSegmentCount = Mathf.CeilToInt( pastAbsoluteDegreeRange/ 360.0f * segmentsPer360);
		    TAincrementRad = pastAbsoluteDegreeRange * Deg2Rad / pastSegmentCount;
		    double futureSegments = futureOrbitDegreeRange * Deg2Rad / TAincrementRad;
		    int futureSegmentCount = Mathf.CeilToInt( (float) futureSegments);
		    segmentCount = pastSegmentCount + futureSegmentCount;
	    }

	    forceGroundTrackUpdate = true;
    }
    

	public void ToggleOsculatingGroundTrackLine(bool isOn)
	{
		if (isOn)
		{
			UpdateOrbitLineSegmentCountAndGroundTrackRange();
			UpdateMarkerAndLineRendererLineThickness(SpacecraftStateUtilities.GetCurrentGroundTrackLineWidth());
		}
		groundTrackLine.enabled = isOn;
		markerSphere.SetActive(isOn);
	}

	public void UpdateMarkerAndLineRendererLineThickness(float newValue)
	{
		markerSphere.transform.localScale = (CelestialBodyStateUtilities.ViewIsLocal?(CelestialBodyStateUtilities.ViewIsSpacecraftLocal?scMarkerScale:planetMarkerScale):helioMarkerScale)*Vector3.one;
		groundTrackLine.startWidth = newValue;
		groundTrackLine.endWidth = newValue;

		forceGroundTrackUpdate = true;
	}
}
