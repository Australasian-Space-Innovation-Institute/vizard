/*
// ISC License

// Copyright (c) 2025, Autonomous Vehicle Systems Lab, University of Colorado at Boulder

// Permission to use, copy, modify, and/or distribute this software for any
// purpose with or without fee is hereby granted, provided that the above
// copyright notice and this permission notice appear in all copies.

// THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
// WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
// MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
// ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
// WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
// ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
// OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

// */
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using VizProtobufferMessage;
/// <summary>
/// Sets up and updates a Quad Map object
/// </summary>

public class QuadMapMethods : MonoBehaviour
{
    private int quadMapID;
    private string parentBodyName;
    private bool parentIsSC;
    private GameObject myLabel;
    public MeshFilter mf;
    public GameObject meshObject;
    public GameObject labelLocation;
    private float parentBodyRadius;
    private readonly float bodyOffsetFactor = 1.002f;

    // SCARLET demo: a quad map labelled "STYLE:volume" or "STYLE:glow" is drawn in
    // that style instead of Vizard's default, and the label is not shown:
    //   volume  a translucent 3D shape: both faces, no depth writes, fading towards
    //           its far end from the planet (a sensor's field of regard)
    //   glow    additive: lightens what is under it (the surface, targets) by its
    //           colour times its alpha, without covering it (the field's footprint)
    // Their vertices are drawn where they are sent (no 1.002 lift; glow 1.00005).
    private const string StylePrefix = "STYLE:";
    private const float GlowLift = 1.00005f;
    private string style = "";
    private static Shader styleShader;
    private bool hiddenByMessage;

    private List<Vector3> vertices = new List<Vector3>();
    private Dictionary<int, VizMessage.Types.QuadMap> myMessages = new Dictionary<int, VizMessage.Types.QuadMap>();
    private int firstIndex;
    private int currentAppliedMsgIndex;
    private Camera cameraToUse;
    
    private readonly int layerMask = ((1 << 7) |(1 << 9) | (1 << 11) | (1 << 24)); //7=UnlitSC, 9=TrueBodySizeColliders, 11 = Spacecraft, 24 = LabelMarkers

    void FixedUpdate()
    {
        int currentIndex = MessageList.CurrentIndex;
        if (currentIndex < firstIndex)
        {
            meshObject.SetActive(false);
            if (myLabel != null)
            {
                myLabel.SetActive(false);
            }
        }else{
            int correctIndex = firstIndex;
            foreach (int msgIndex in myMessages.Keys)
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
                ApplyQuadMapSettings(myMessages[correctIndex]);
                currentAppliedMsgIndex = correctIndex;
            }

            if (VizardGUISettings.ShowQuadMapLabels)
            {
                myLabel.GetComponent<TextMeshProUGUI>().enabled = CheckLabelLocationVisibleInCamera();
            }

            // A volume reaches from a planet up to a spacecraft: in the spacecraft-local
            // view the two are drawn at different scales and it would not meet the
            // spacecraft, so it is not drawn there.
            if (style == "volume")
            {
                meshObject.SetActive(!hiddenByMessage && !CelestialBodyStateUtilities.ViewIsSpacecraftLocal);
            }
        }
    }

    public bool InitializeQuadMap(VizMessage.Types.QuadMap myMapMsg, int msgIndex)
    {
        cameraToUse = MainCameraUtilities.MainCamera;
        myMessages[msgIndex] = myMapMsg;
        firstIndex = msgIndex;
        quadMapID = myMapMsg.ID;

        bool parentFound = false;
        if (myMapMsg.ParentBodyName != "")
        {
            parentFound = SetParentBody(myMapMsg.ParentBodyName);
        }
        else
        {
            VizardGUISettings.UpdateErrorMessages($"Quad Map {myMapMsg.ID}: Could not build quad map because Parent Body Name was empty string.", true);
        }

        if (parentFound)
        {
            ApplyStyle(myMapMsg.Label);
            CreateQuads(myMapMsg.Vertices.ToArray());
            if (myMapMsg.Color.Count >= 3)
            {
                SetColor(myMapMsg);
            }
            else
            {
                VizardGUISettings.UpdateErrorMessages($"Quad Map {myMapMsg.ID}: Could not build quad map because color was not specified.", true);
                return false;
            }

            name = "QuadMap" + quadMapID;
            InitializeLocationMarker();
            SetLabel(IsStyleLabel(myMapMsg.Label) ? "NOLABEL" : myMapMsg.Label);

            return true;
        }
        return false;
    }

    public void UpdateQuadMapSettings(VizMessage.Types.QuadMap myMapMsg, int msgIndex)
    {
        myMessages[msgIndex] = myMapMsg;
        ApplyQuadMapSettings(myMapMsg);
        currentAppliedMsgIndex = msgIndex;
    }
    private void ApplyQuadMapSettings(VizMessage.Types.QuadMap myMapMsg)
    {
        hiddenByMessage = myMapMsg.IsHidden;
        if (myMapMsg.IsHidden)
        {
            meshObject.SetActive(false);
            if (myLabel != null)
            {
                myLabel.SetActive(false);
            }
        }
        else
        {
            meshObject.SetActive(true);
            if (myLabel != null)
            {
                myLabel.SetActive(VizardGUISettings.ShowQuadMapLabels);
            }

            if (myMapMsg.ParentBodyName != "")
            {
                SetParentBody(myMapMsg.ParentBodyName);
            }
            ApplyStyle(myMapMsg.Label);

            if (myMapMsg.Vertices.Count > 0)
            {
                CreateQuads(myMapMsg.Vertices.ToArray());
                InitializeLocationMarker();
            }

            if (myMapMsg.Color.Count >= 3)
            {
                SetColor(myMapMsg);
            }

            if (myMapMsg.Label != "" && !IsStyleLabel(myMapMsg.Label))
            {
                string newText = (myMapMsg.Label == "NOLABEL") ? "" : myMapMsg.Label;
                UpdateLabel(newText);
            }
        }

    }

    private bool SetParentBody(string bodyName)
    {
        if (bodyName != parentBodyName)
        {
            GameObject newParent = CelestialBodyStateUtilities.GetGameObjectWithBodyName(bodyName);
            if (newParent != null)
            {
                transform.SetParent(newParent.transform);
                transform.localPosition = Vector3.zero;
                transform.localScale = bodyOffsetFactor * Vector3.one;
                transform.localRotation = Quaternion.Euler(0, 0, 0);
                
                if (newParent.CompareTag("Spacecraft"))
                {
                    parentBodyRadius = 1f;
                    ConfigureHUDForSpriteMode(newParent.GetComponent<SpacecraftController>().GetSpriteOnLastFrame());
                }
                else
                {
                    if (!newParent.CompareTag("Sun"))
                    {
                        parentBodyRadius = newParent.GetComponent<PlanetController>().planetRadius;
                        ConfigureHUDForSpriteMode(newParent.GetComponent<PlanetController>().planetSprite.activeSelf);
                    }
                    else
                    {
                        parentBodyRadius = newParent.GetComponent<SunBuilder>().sunRadius;
                    }
                }
                return true;
            }
            VizardGUISettings.UpdateErrorMessages($"QuadMap {quadMapID} failed: Could not find \"{bodyName}\" in instantiated objects. Matching is case-sensitive.");
            return false;
        }
        return true;
    }
    private void CreateQuads(double[] quadVertices)
    {
        mf.mesh.Clear();
        CreateVertices(quadVertices);
        CreateTris();
        CreateNormals();
        CreateUV();
        if (style != "")
        {
            CreateColors();
        }
    }

    private static bool IsStyleLabel(string label)
    {
        return label.StartsWith(StylePrefix);
    }

    private void ApplyStyle(string label)
    {
        if (!IsStyleLabel(label))
        {
            return;
        }
        string newStyle = label.Substring(StylePrefix.Length);
        transform.localScale = (newStyle == "glow" ? GlowLift : 1f) * Vector3.one;
        if (newStyle == style)
        {
            return;
        }
        style = newStyle;
        if (styleShader == null)
        {
            styleShader = Shader.Find("Hidden/Internal-Colored");
        }
        Material material = new Material(styleShader);
        material.SetInt("_Cull", (int) UnityEngine.Rendering.CullMode.Off);
        material.SetInt("_ZWrite", 0);
        if (style == "glow")
        {
            material.SetInt("_SrcBlend", (int) UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_DstBlend", (int) UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_ZBias", -1f);
            material.renderQueue = 3001;
        }
        else
        {
            material.SetInt("_SrcBlend", (int) UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int) UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.renderQueue = 3002;
        }
        meshObject.GetComponent<MeshRenderer>().material = material;
    }

    private void CreateColors()
    {
        // The volume: full colour at the end nearest the planet, 40% at the far end.
        Color[] colors = new Color[vertices.Count];
        float rMin = float.MaxValue, rMax = float.MinValue;
        foreach (Vector3 v in vertices)
        {
            rMin = Mathf.Min(rMin, v.magnitude);
            rMax = Mathf.Max(rMax, v.magnitude);
        }
        float span = Mathf.Max(rMax - rMin, 1e-6f);
        for (int i = 0; i < vertices.Count; i++)
        {
            float a = style == "volume" ? 1f - 0.6f * (vertices[i].magnitude - rMin) / span : 1f;
            colors[i] = new Color(1f, 1f, 1f, a);
        }
        mf.mesh.colors = colors;
    }
    private void CreateVertices(double[] vertexArray)
    {
        vertices = new List<Vector3>();
        for (int i = 0; i < vertexArray.Length; i += 3)
        {
            //Convert to Unity CS from BSK CS for each vertex
            Vector3 newVector = new Vector3((float) vertexArray[i + 1], (float) vertexArray[i + 2], (float) -vertexArray[i]);
            newVector /= parentBodyRadius;
            vertices.Add(newVector);
        }
        mf.mesh.vertices= vertices.ToArray();
    }

    private void CreateTris()
    {
        int[] tri = new int[vertices.Count*3]; // 4 vertices per quad, 2 triangles per quad, 3 tris per triangle, x2 to have a face on both sides

        int j = 0;
         for (int i = 0; i < vertices.Count; i+=4)
         {
             tri[j] = i;
             tri[j + 1] = i + 2;
             tri[j + 2] = i + 1;
        
             tri[j + 3] = i ;
             tri[j + 4] = i + 3;
             tri[j + 5] = i + 2;
             j += 6;
         }
         
        for (int i = 0; i < vertices.Count; i+=4)
        {
            tri[j] = i;
            tri[j + 1] = i + 1;
            tri[j + 2] = i + 2;

            tri[j + 3] = i + 2;
            tri[j + 4] = i + 3;
            tri[j + 5] = i;
            j += 6;
        }

        mf.mesh.triangles = tri;
    }

    private void CreateNormals()
    {
        //mf.mesh.normals = vertices.ToArray();
        Vector3[] normals = new Vector3[vertices.Count];
        for (int i = 0; i < vertices.Count; i+=4)
        {
            normals[i] = vertices[i];
            normals[i+1] = vertices[i];
            normals[i+2] = vertices[i];
            normals[i+3] = vertices[i];
        }

        mf.mesh.normals = normals;
    }

    private void CreateUV()
    {
        Vector2[] uv = new Vector2[vertices.Count];

        for (int k = 0; k < vertices.Count; k+=4)
        {
            uv[k] = new Vector2(0, 0);
            uv[k+1] = new Vector2(1, 0);
            uv[k+2] = new Vector2(0, 1);
            uv[k+3] = new Vector2(1, 1);
        }

        mf.mesh.uv = uv;
    }

    private void SetColor(VizMessage.Types.QuadMap myMapMsg)
    {
        if (style == "glow")
        {
            float a = myMapMsg.Color.Count >= 4 ? myMapMsg.Color[3] / 255f : 1f;
            meshObject.GetComponent<MeshRenderer>().material.color = new Color(a * myMapMsg.Color[0] / 255f,
                a * myMapMsg.Color[1] / 255f, a * myMapMsg.Color[2] / 255f, 1f);
        }
        else if (myMapMsg.Color.Count >= 4)
        {
            meshObject.GetComponent<MeshRenderer>().material.color = new Color(myMapMsg.Color[0] / 255f,
                myMapMsg.Color[1] / 255f, myMapMsg.Color[2] / 255f, myMapMsg.Color[3] / 255f);
        }
        else if (myMapMsg.Color.Count == 3)
        {
            meshObject.GetComponent<MeshRenderer>().material.color = new Color(myMapMsg.Color[0] / 255f,
                myMapMsg.Color[1] / 255f, myMapMsg.Color[2] / 255f, 1.0f);
        }
    }

    private void InitializeLocationMarker()
    {
        Bounds myBounds = SpacecraftStateUtilities.CalculateModelBounds(meshObject);
        labelLocation.transform.position = 1.1f*myBounds.center;
    }

    private void SetLabel(string labelText)
    {
        string textToSet = (labelText == "NOLABEL") ? "" : labelText;
        myLabel = LabelMaker.CreateLabel(textToSet, "Label", labelLocation, new Vector2(0, 0), "QuadMaps", 0);
        myLabel.SetActive(VizardGUISettings.ShowQuadMapLabels&&(textToSet!=""));
    }

    private void UpdateLabel(string labelText)
    {
        myLabel.GetComponent<TextMeshProUGUI>().text = labelText;
        myLabel.SetActive(VizardGUISettings.ShowQuadMapLabels&&(labelText!=""));
    }
    /// <summary>
    /// This receives a BroadcastMessage from the parent Spacecraft when
    /// going into sprite mode. Don't delete. 
    /// </summary>
    /// <param name="spriteOn">True if attached spacecraft is in sprite mode</param>
    private void ConfigureHUDForSpriteMode(bool spriteOn)
    {
        meshObject.SetActive(!spriteOn);
        if (myLabel != null)
        {
            myLabel.SetActive(!spriteOn);
        }
    }

    private bool CheckLabelLocationVisibleInCamera()
    {
        if (DataManager.FirstMessageDisplayed)
        {
            Vector3 origin = cameraToUse.transform.position;
            Vector3 direction = labelLocation.transform.position - origin;
            float maxDistance = direction.magnitude * 1.3f;

            if (Physics.Raycast(origin, direction, out var hit, maxDistance, layerMask))
            {
                if (labelLocation == hit.collider.gameObject)
                {
                    return true;
                }
            }
            return false;
        }

        return false;
    }
}
