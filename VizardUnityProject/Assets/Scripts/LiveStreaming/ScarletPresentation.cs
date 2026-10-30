/*
 Scarlet Alpha demo app: satellite framing and fault rings, requested over the
 direct-communication socket (DirectCommunicationController).

 Ported from RTelikani's camera bridge (scarlet-alpha wp2 branch,
 integrated_demo/examples/joindemo/vizard_camera/CameraBridge.cs), which
 patched these behaviours into a released Vizard; here they are in the source.

 - "CAMERA <spacecraft>" (FrameSpacecraft): Ray's Earth-relative framing,
   looking down from 25 degrees off its Earth-facing top, at a distance that
   fits its model. Not used since 2026-10-29: it set the camera every frame,
   so the presenter could not orbit; "CAMERA <name>" now goes to Vizard's own
   camera targeting (DirectCommunicationController.HandleCameraCommand).
 - "FAULTS a,b": a flashing translucent red ring around those spacecraft
   (wall-clock flashing, so it shows while paused); "FAULTS" clears it.
 - "SPACECRAFT_SIZE x": every spacecraft's model drawn at scale x in planet
   local view, the same for all (Vizard's DefaultLocalViewSpacecraftScale, the
   Settings panel's field; Vizard's default is 5), held across new runs.
 - "MODELS a,b": the app's choice of model or sprite per spacecraft: those
   spacecraft are always drawn as their models and every other one always as
   Vizard's coloured sprite, at any distance, the camera target included
   (SpacecraftController.UpdateSprite asks ModelChoice). "MODELS" alone: all
   sprites. "MODELS auto" (the start): Vizard's own rule (a sprite when far
   off or small on screen; the camera target is its model). Vizard logs
   "models_drawn names=a,b" whenever the spacecraft drawn as models change,
   so the app's boxes can show Vizard's own choice before the app makes one.
 - "CAMERA cluster" (ViewCluster): the whole cluster in Vizard's own camera,
   so it can be orbited and zoomed like any target: the spacecraft nearest
   the cluster's middle becomes the camera target, the view zooms out to
   planet local scale (Vizard keeps following that spacecraft), and the
   camera is put above it, radially out from Earth, far enough to fit every
   spacecraft. Done over a few frames so each Vizard transition completes.
*/

using System;
using System.Collections.Generic;
using UnityEngine;

public class ScarletPresentation : MonoBehaviour
{
    private static ScarletPresentation instance;
    private static volatile string pendingFaults; // from the socket thread; applied on the main thread
    private static volatile float spacecraftSize;  // "SPACECRAFT_SIZE x"; 0: Vizard's own
    private static volatile string pendingModels;  // "MODELS a,b" from the socket thread
    private static readonly HashSet<string> models = new HashSet<string>(); // main thread only
    private static bool modelsChosen;              // false: Vizard's own sprite rule ("MODELS auto")
    private static string drawnReported;           // the last "models_drawn" reported
    private static float nextDrawnCheck;

    private string framed;                 // spacecraft being framed; null: Vizard's own view
    private string measuredFor;
    private DateTime nextMeasure;
    private float radius;
    private Vector3 side;
    private bool saved;
    private float savedFov, savedNear, savedFar;
    private readonly Dictionary<Renderer, bool> hidden = new Dictionary<Renderer, bool>();
    private readonly HashSet<string> faulty = new HashSet<string>();
    private Texture2D faultRing;
    private string waitingFor;

    private enum ClusterStep { None, Targeting, ZoomingOut }
    private ClusterStep clusterStep;
    private GameObject clusterCentre;
    private int clusterFrames;

    /// <summary>Frame a spacecraft (main thread). False: no such spacecraft in the scene.</summary>
    public static bool FrameSpacecraft(string name)
    {
        if (FindSpacecraft(name) == null)
        {
            return false;
        }
        Ensure().framed = name;
        return true;
    }

    /// <summary>End the framing: Vizard's own camera view again (main thread).</summary>
    public static void StopFraming()
    {
        if (instance != null)
        {
            instance.Release();
        }
    }

    /// <summary>
    /// Show the whole cluster (main thread). False: no spacecraft in the scene yet.
    /// </summary>
    public static bool ViewCluster()
    {
        List<GameObject> crafts = SpacecraftStateUtilities.ParentSpacecraftList;
        MainCameraViewManager manager = Manager();
        if (crafts == null || manager == null)
        {
            return false;
        }
        Vector3 centroid = Vector3.zero;
        int count = 0;
        foreach (GameObject craft in crafts)
        {
            if (craft != null)
            {
                centroid += craft.transform.position;
                count++;
            }
        }
        if (count == 0)
        {
            return false;
        }
        centroid /= count;
        GameObject centre = null;
        float nearest = float.MaxValue;
        foreach (GameObject craft in crafts)
        {
            if (craft == null) continue;
            float distance = (craft.transform.position - centroid).sqrMagnitude;
            if (distance < nearest)
            {
                nearest = distance;
                centre = craft;
            }
        }
        ScarletPresentation self = Ensure();
        self.Release();
        manager.SetupChangeOfMainCameraTarget(centre);
        self.clusterCentre = centre;
        self.clusterStep = ClusterStep.Targeting;
        self.clusterFrames = 0;
        return true;
    }

    private static bool PlanetLocal()
    {
        return CelestialBodyStateUtilities.ViewIsLocal && !CelestialBodyStateUtilities.ViewIsSpacecraftLocal;
    }

    private static MainCameraViewManager Manager()
    {
        Camera main = MainCameraUtilities.MainCamera;
        return main == null ? null : main.GetComponent<MainCameraViewManager>();
    }

    private void LateUpdate()
    {
        if (clusterStep == ClusterStep.None)
        {
            return;
        }
        MainCameraViewManager manager = Manager();
        if (manager == null || clusterCentre == null || MainCameraUtilities.CameraTarget != clusterCentre)
        {
            clusterStep = ClusterStep.None;      // the presenter picked another target meanwhile
            return;
        }
        clusterFrames++;
        if (clusterStep == ClusterStep.Targeting)
        {
            if (clusterFrames < 3)
            {
                return;                           // let Vizard finish the change of target
            }
            // ViewIsLocal is also true in the close-up spacecraft view; the
            // cluster needs planet local scale, so zoom out unless already there.
            if (!PlanetLocal())
            {
                manager.triggerZoomTransitionToLocalView = true;
            }
            clusterStep = ClusterStep.ZoomingOut;
            clusterFrames = 0;
            return;
        }
        if (!PlanetLocal() || manager.triggerZoomTransitionToLocalView || clusterFrames < 3)
        {
            if (clusterFrames > 300)
            {
                Debug.LogWarning("[SCARLET] cluster view: planet local view never came");
                clusterStep = ClusterStep.None;
            }
            return;
        }
        FitCluster(manager);
        clusterStep = ClusterStep.None;
    }

    /// <summary>Put the camera above the centre spacecraft, far enough to see them all.</summary>
    private void FitCluster(MainCameraViewManager manager)
    {
        Vector3 centre = clusterCentre.transform.position;
        float span = 0f;
        foreach (GameObject craft in SpacecraftStateUtilities.ParentSpacecraftList)
        {
            if (craft != null)
            {
                span = Math.Max(span, (craft.transform.position - centre).magnitude);
            }
        }
        Camera view = MainCameraUtilities.MainCamera;
        float halfAngle = (float)Math.Atan(Math.Tan(view.fieldOfView * Math.PI / 360.0)
                                           * Math.Min(1.0, Math.Max(0.1, view.aspect)));
        // Never closer than Vizard's own planet local distance to the centre spacecraft.
        float least = (manager.cameraRigTransform.position - centre).magnitude;
        float distance = Math.Max(span * 1.4f / (float)Math.Sin(halfAngle), least);
        GameObject earth = CelestialBodyStateUtilities.GetGameObjectWithBodyName("earth");
        Vector3 up = earth != null ? centre - earth.transform.position : manager.cameraRigTransform.position - centre;
        if (up.sqrMagnitude <= 0f)
        {
            up = Vector3.up;
        }
        manager.cameraRigTransform.position = centre + up.normalized * distance;
        manager.SetOffset();
        manager.cameraRigTransform.LookAt(clusterCentre.transform);
        Debug.Log("[SCARLET] cluster view on " + clusterCentre.name + " span=" + span + " distance=" + distance);
        VizardCommandLog.Log("cluster_view", $"centre={VizardCommandLog.Value(clusterCentre.name)} span={span:0.##} distance={distance:0.##}");
    }

    /// <summary>"FAULTS a,b" from the socket thread.</summary>
    public static void SetFaultsFromRequest(string request)
    {
        int start = request.IndexOf("FAULTS", StringComparison.Ordinal);
        pendingFaults = start < 0 ? "" : request.Substring(start + 6).Trim();
    }

    /// <summary>"MODELS a,b" from the socket thread.</summary>
    public static void SetModelsFromRequest(string request)
    {
        int start = request.IndexOf("MODELS", StringComparison.Ordinal);
        pendingModels = start < 0 ? "" : request.Substring(start + 6).Trim();
    }

    /// <summary>
    /// The app's choice for this spacecraft (main thread): 1 its model, -1 a
    /// sprite, 0 no choice made ("MODELS auto"): Vizard's own rule.
    /// </summary>
    public static int ModelChoice(string name)
    {
        if (!modelsChosen || name == null)
        {
            return 0;
        }
        return models.Contains(name) ? 1 : -1;
    }

    /// <summary>
    /// Log "models_drawn names=a,b" when the spacecraft Vizard draws as their
    /// models (not sprites) change: the app ticks their "Show as model" boxes.
    /// Checked 5 times a second.
    /// </summary>
    private static void ReportModelsDrawn()
    {
        if (Time.unscaledTime < nextDrawnCheck)
        {
            return;
        }
        nextDrawnCheck = Time.unscaledTime + 0.2f;
        var drawn = new List<string>();
        List<GameObject> crafts = SpacecraftStateUtilities.ParentSpacecraftList;
        if (crafts != null)
        {
            foreach (GameObject craft in crafts)
            {
                if (craft == null) continue;
                SpacecraftController controller = craft.GetComponent<SpacecraftController>();
                if (controller != null && !controller.GetSpriteOnLastFrame())
                {
                    drawn.Add(craft.name.Replace(' ', '_').Replace(',', '_'));
                }
            }
        }
        drawn.Sort(StringComparer.Ordinal);
        string names = string.Join(",", drawn);
        if (names != drawnReported)
        {
            drawnReported = names;
            VizardCommandLog.Log("models_drawn", "names=" + names);
        }
    }

    /// <summary>"SPACECRAFT_SIZE x" from the socket thread. False: not a size above 0.</summary>
    public static bool SetSpacecraftSizeFromRequest(string request)
    {
        int start = request.IndexOf("SPACECRAFT_SIZE", StringComparison.Ordinal);
        float size;
        if (start < 0 || !float.TryParse(request.Substring(start + 15).Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out size) || size <= 0f)
        {
            return false;
        }
        spacecraftSize = size;
        return true;
    }

    /// <summary>Apply pending FAULTS, MODELS and SPACECRAFT_SIZE requests (main thread, every frame).</summary>
    public static void Poll()
    {
        string modelRequest = pendingModels;
        if (modelRequest != null)
        {
            pendingModels = null;
            models.Clear();
            modelsChosen = !string.Equals(modelRequest, "auto", StringComparison.OrdinalIgnoreCase);
            if (modelsChosen)
            {
                foreach (string name in modelRequest.Split(','))
                {
                    if (name.Trim().Length > 0)
                    {
                        models.Add(name.Trim());
                    }
                }
            }
            Debug.Log(modelsChosen
                ? "[SCARLET] shown as models: " + string.Join(", ", models) + "; the rest as sprites"
                : "[SCARLET] models or sprites: Vizard's own rule");
        }
        // Every spacecraft's size in planet local view, the same for all (the
        // close-up spacecraft view stays true to scale). Held every frame: a new
        // run puts Vizard's default (5) back.
        float size = spacecraftSize;
        if (size > 0f && SpacecraftStateUtilities.DefaultLocalViewSpacecraftScale != size)
        {
            SpacecraftStateUtilities.DefaultLocalViewSpacecraftScale = size;
            Debug.Log("[SCARLET] spacecraft size " + size);
        }
        ReportModelsDrawn();
        string request = pendingFaults;
        if (request == null)
        {
            return;
        }
        pendingFaults = null;
        var names = new HashSet<string>();
        foreach (string name in request.Split(','))
        {
            if (name.Trim().Length > 0)
            {
                names.Add(name.Trim());
            }
        }
        ScarletPresentation self = Ensure();
        if (!self.faulty.SetEquals(names))
        {
            self.faulty.Clear();
            self.faulty.UnionWith(names);
            Debug.Log("[SCARLET] fault rings: " + string.Join(", ", names));
        }
    }

    private static ScarletPresentation Ensure()
    {
        if (instance == null)
        {
            var host = new GameObject("ScarletPresentation");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<ScarletPresentation>();
        }
        return instance;
    }

    private void OnEnable()
    {
        Camera.onPreCull += Frame;
        Camera.onPostRender += RestoreRenderers;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= Frame;
        Camera.onPostRender -= RestoreRenderers;
    }

    private static GameObject FindSpacecraft(string name)
    {
        if (SpacecraftStateUtilities.ParentSpacecraftList == null)
        {
            return null;
        }
        foreach (GameObject craft in SpacecraftStateUtilities.ParentSpacecraftList)
        {
            if (craft != null && craft.name == name)
            {
                return craft;
            }
        }
        return null;
    }

    private static bool IsCameraOverlay(Renderer renderer)
    {
        string name = renderer.name;
        return name == "VisibleCone" || name.StartsWith("hemisphere") ||
               name == "CameraFrustrum" || name.StartsWith("CameraImageQuad-");
    }

    private void Release()
    {
        Camera view = MainCameraUtilities.MainCamera;
        if (saved && view != null)
        {
            view.fieldOfView = savedFov;
            view.nearClipPlane = savedNear;
            view.farClipPlane = savedFar;
        }
        saved = false;
        framed = null;
        measuredFor = null;
    }

    private void RestoreRenderers(Camera view)
    {
        if (view != MainCameraUtilities.MainCamera)
        {
            return;
        }
        foreach (KeyValuePair<Renderer, bool> entry in hidden)
        {
            if (entry.Key != null)
            {
                entry.Key.forceRenderingOff = entry.Value;
            }
        }
        hidden.Clear();
    }

    private void Hide(GameObject craft, bool overlaysOnly)
    {
        foreach (Renderer renderer in craft.GetComponentsInChildren<Renderer>())
        {
            if (overlaysOnly && !IsCameraOverlay(renderer))
            {
                continue;
            }
            if (!hidden.ContainsKey(renderer))
            {
                hidden[renderer] = renderer.forceRenderingOff;
            }
            renderer.forceRenderingOff = true;
        }
    }

    private void Frame(Camera view)
    {
        if (framed == null || view == null || view != MainCameraUtilities.MainCamera)
        {
            return;
        }
        RestoreRenderers(view);
        try
        {
            GameObject craft = FindSpacecraft(framed);
            GameObject earth = CelestialBodyStateUtilities.GetGameObjectWithBodyName("earth");
            if (craft == null || earth == null)
            {
                if (waitingFor != framed)
                {
                    Debug.Log("[SCARLET] camera waiting for scene object: " + framed);
                }
                waitingFor = framed;
                return;
            }
            Vector3 radial = craft.transform.position - earth.transform.position;
            if (radial.sqrMagnitude <= 0f)
            {
                return;
            }
            radial.Normalize();
            if (measuredFor != framed || DateTime.UtcNow >= nextMeasure)
            {
                nextMeasure = DateTime.UtcNow.AddSeconds(1);
                float measured = 0f;
                foreach (Renderer renderer in craft.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                    if (renderer.GetComponent<TextMesh>() != null || IsCameraOverlay(renderer)) continue;
                    Bounds bounds = renderer.localBounds;
                    Vector3 centre = renderer.transform.TransformPoint(bounds.center);
                    float extent = Vector3.Scale(bounds.extents, renderer.transform.lossyScale).magnitude
                                   + (centre - craft.transform.position).magnitude;
                    measured = Math.Max(measured, extent);
                }
                // Vizard first shows a tiny placeholder and loads the detailed
                // model later: framing the placeholder would put the camera
                // inside the final model, so wait for it.
                if (measured <= 0f)
                {
                    return;
                }
                if (measuredFor != framed)
                {
                    side = Vector3.Cross(radial, Vector3.up);
                    if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(radial, Vector3.right);
                    Debug.Log("[SCARLET] framing " + framed + " radius=" + measured);
                }
                radius = measured;
                measuredFor = framed;
            }
            if (!saved)
            {
                savedFov = view.fieldOfView;
                savedNear = view.nearClipPlane;
                savedFar = view.farClipPlane;
                saved = true;
            }
            // Look down from 25 degrees off the Earth-facing top direction,
            // Earth-relative as the craft turns.
            side = Vector3.ProjectOnPlane(side, radial);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(radial, Vector3.up);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(radial, Vector3.right);
            side.Normalize();
            Vector3 direction = (radial + 0.4663f * side).normalized;
            view.fieldOfView = 35f;
            float halfAngle = (float)Math.Atan(Math.Tan(35.0 * Math.PI / 360.0)
                                               * Math.Min(1.0, Math.Max(0.1, view.aspect)));
            float distance = radius * 1.55f / (float)Math.Sin(halfAngle);
            view.nearClipPlane = Math.Max(0.00001f, distance * 0.001f);
            view.farClipPlane = Math.Max(savedFar, distance + radius * 10f);
            view.transform.position = craft.transform.position + direction * distance;
            Vector3 aim = craft.transform.position - radial * radius * 0.45f;
            view.transform.rotation = Quaternion.LookRotation(aim - view.transform.position, radial);
            Hide(craft, true);
            foreach (GameObject peer in SpacecraftStateUtilities.ParentSpacecraftList)
            {
                if (peer != null && peer != craft)
                {
                    Hide(peer, false);
                }
            }
        }
        catch (Exception error)
        {
            Debug.LogWarning("[SCARLET] camera: " + error.Message);
        }
    }

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || faulty.Count == 0)
        {
            return;
        }
        // Wall-clock flashing: it keeps flashing while the simulation is paused.
        if (Time.realtimeSinceStartup % 1f >= 0.6f)
        {
            return;
        }
        Camera view = MainCameraUtilities.MainCamera;
        if (view == null)
        {
            return;
        }
        if (faultRing == null)
        {
            const int size = 256;
            faultRing = new Texture2D(size, size, TextureFormat.RGBA32, false);
            faultRing.hideFlags = HideFlags.HideAndDontSave;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = new Vector2(x - 127.5f, y - 127.5f).magnitude;
                    float alpha = 0.38f * Mathf.Clamp01(121f - distance); // translucent filled halo
                    faultRing.SetPixel(x, y, new Color(1f, 0.22f, 0.22f, alpha));
                }
            }
            faultRing.Apply(false, true);
        }
        int previousDepth = GUI.depth;
        GUI.depth = -1000;
        foreach (string name in faulty)
        {
            if (framed != null && framed != name)
            {
                continue; // a framed view hides the other spacecraft
            }
            GameObject craft = FindSpacecraft(name);
            if (craft == null || !craft.activeInHierarchy)
            {
                continue;
            }
            Vector3 centre = view.WorldToScreenPoint(craft.transform.position);
            if (centre.z <= 0 || !view.pixelRect.Contains(new Vector2(centre.x, centre.y)))
            {
                continue;
            }
            float radiusPixels = 16f;
            foreach (Renderer renderer in craft.GetComponentsInChildren<Renderer>())
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (renderer.GetComponent<TextMesh>() != null || IsCameraOverlay(renderer)) continue;
                Bounds local = renderer.localBounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 sign = new Vector3((corner & 1) == 0 ? -1 : 1,
                        (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
                    Vector3 point = view.WorldToScreenPoint(renderer.transform.TransformPoint(
                        local.center + Vector3.Scale(local.extents, sign)));
                    if (point.z > 0)
                    {
                        radiusPixels = Math.Max(radiusPixels, Vector2.Distance(
                            new Vector2(point.x, point.y), new Vector2(centre.x, centre.y)) + 8f);
                    }
                }
            }
            float maximum = framed == null ? 32f : Math.Min(Screen.width, Screen.height) * 0.3f;
            radiusPixels = Math.Min(radiusPixels, maximum);
            float halfSize = radiusPixels * 128f / 121f; // the halo is inset from the texture's edges
            GUI.DrawTexture(new Rect(centre.x - halfSize, Screen.height - centre.y - halfSize,
                halfSize * 2f, halfSize * 2f), faultRing);
        }
        GUI.depth = previousDepth;
    }
}
