using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BrailleSoundDemo
{
    [Serializable]
    public sealed class RecognizedWordObservation
    {
        public int groupIndex;
        public string groupId;
        public string rawText;
        public string word;
        public bool isFinish;
        public string[] componentIds;
        public Vector3 worldPosition;
    }

    public sealed class LetterComponentView
    {
        public string id;
        public string letter;
        public int groupIndex;
        public Transform transform;
        public Vector3 initialPosition;
        public Quaternion initialRotation;
    }

    /// <summary>Fixture motion + geometric readback. Replace this component with a detector adapter later.</summary>
    public sealed class MockLetterLayout : MonoBehaviour
    {
        public event Action<RecognizedWordObservation> Recognized;
        public IReadOnlyList<LetterComponentView> Components => components;
        public int ComponentCount => components.Count;
        public Transform DetectionOrigin { get; private set; }
        public RecognizedWordObservation[] Observations { get; } = new RecognizedWordObservation[3];
        public float AssemblySeconds = .85f;
        public static readonly string[] GroupIds = { "word-group-01", "word-group-02", "word-group-03" };
        readonly List<LetterComponentView> components = new List<LetterComponentView>();
        const float LetterSpacing = .29f, LetterHalfWidth = .125f, LetterHalfDepth = .14f;
        const float TableHalfWidth = 2.2f, MinimumCenterDistance = 1.25f;
        static readonly int[] FixtureLengths = { 3, 4, 6 };
        readonly Vector3[] centers = new Vector3[3];
        readonly Transform[] groups = new Transform[3];
        readonly bool[] assembled = new bool[3];
        readonly List<Material> materials = new List<Material>();
        int layoutGeneration;
        public bool IsAssembled(int index) => index >= 0 && index < 3 && assembled[index];
        public Transform GroupTransform(int index) => groups[index];
        public Vector3 GroupWorldPosition(int index) => groups[index].position;
        /// <summary>
        /// Random fixture centers in board-local metres. Letter extents stay inside
        /// the table, away from the front controls/back probes, and do not overlap.
        /// Only the mock producer uses these bounds; measured external poses are not clamped.
        /// </summary>
        public static Vector3[] GenerateRandomGroupPositions()
        {
            var result = new Vector3[3];
            // Restart the whole layout after an unsuccessful placement. This keeps
            // all words free to appear in any part of the usable tabletop.
            for (int attempt = 0; attempt < 256; attempt++)
            {
                bool valid = true;
                for (int i = 0; i < result.Length; i++)
                {
                    float limit = TableHalfWidth - HalfWidth(i) - .08f;
                    result[i] = new Vector3(UnityEngine.Random.Range(-limit, limit), .09f,
                        UnityEngine.Random.Range(-1.04f, .92f));
                    for (int j = 0; j < i; j++)
                        if (!Separated(i, result[i], j, result[j])) { valid = false; break; }
                    if (!valid) break;
                }
                if (valid) return result;
            }
            // A bounded fallback guarantees completion even for an unlucky random
            // sequence; permuting these safe slots still varies the word identities.
            var fallback = new[] { new Vector3(-1.15f,.09f,-.90f), new Vector3(1.15f,.09f,-.90f), new Vector3(0,.09f,.75f) };
            for (int i = fallback.Length - 1; i > 0; i--)
            {
                int other = UnityEngine.Random.Range(0, i + 1);
                var saved = fallback[i]; fallback[i] = fallback[other]; fallback[other] = saved;
            }
            return fallback;
        }
        static float HalfWidth(int index) => (FixtureLengths[index] - 1) * LetterSpacing * .5f + LetterHalfWidth;
        static bool Separated(int a, Vector3 pa, int b, Vector3 pb)
        {
            if (Vector3.Distance(pa, pb) < MinimumCenterDistance) return false;
            return Mathf.Abs(pa.x - pb.x) >= HalfWidth(a) + HalfWidth(b) + .12f ||
                   Mathf.Abs(pa.z - pb.z) >= LetterHalfDepth * 2 + .16f;
        }
        /// <summary>Visual pose only, in DetectionOrigin-local metres. Does not emit recognition.</summary>
        public bool SetGroupPose(int index, Vector3 localPosition, Quaternion localRotation)
        {
            if (index < 0 || index >= groups.Length || groups[index] == null || !Finite(localPosition) || !Finite(localRotation)) return false;
            groups[index].SetLocalPositionAndRotation(localPosition, localRotation.normalized);
            return true;
        }
        /// <summary>
        /// Renders the fixture letters at an accepted external pose without creating
        /// an input event. The shared input pipeline owns the external group/letter IDs.
        /// </summary>
        public bool DisplayCompletedGroup(int index, Vector3 localPosition, Quaternion localRotation)
        {
            if (!SetGroupPose(index, localPosition, localRotation)) return false;
            PlaceLettersInGroup(index);
            assembled[index] = true;
            Observations[index] = null;
            return true;
        }
        public IEnumerator AssembleAt(int index, Vector3 localPosition, Quaternion localRotation)
        {
            if (!SetGroupPose(index, localPosition, localRotation)) yield break;
            yield return Assemble(index);
        }
        public void Initialize(Transform origin)
        {
            if (components.Count != 0) return;
            DetectionOrigin = origin;
            var fixtures = new[] { "CAT", "RAIN", "SLEEP#" };
            var scatter = new[] {
                new Vector2(-1.75f,-.95f),new Vector2(.95f,.25f),new Vector2(-.65f,1.08f),
                new Vector2(1.7f,-.95f),new Vector2(-1.25f,.42f),new Vector2(.1f,-.92f),new Vector2(1.25f,1.05f),
                new Vector2(-.85f,-.52f),new Vector2(1.82f,.22f),new Vector2(-1.8f,1.03f),new Vector2(.2f,.85f),new Vector2(.85f,-.52f),new Vector2(-.22f,.05f)
            };
            int n = 0;
            for (int group = 0; group < 3; group++)
            {
                groups[group] = new GameObject(GroupIds[group]).transform;
                groups[group].SetParent(origin, false); groups[group].localPosition = centers[group];
                for (int letter = 0; letter < fixtures[group].Length; letter++,n++)
                {
                    string glyph = fixtures[group][letter].ToString();
                    string id = "letter-" + (n+1).ToString("D2") + "-" + (glyph == "#" ? "finish" : glyph.ToLowerInvariant());
                    var root = new GameObject(id).transform; root.SetParent(origin, false);
                    var position = new Vector3(scatter[n].x,.09f,scatter[n].y);
                    var rotation = Quaternion.Euler(0, ((n*37)%47)-23, 0);
                    var color = group == 0 ? new Color(.35f,.92f,.70f) : group == 1 ? new Color(.48f,.70f,1f) : new Color(1,.68f,.43f);
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.name = "Letter body"; cube.transform.SetParent(root,false); cube.transform.localScale = new Vector3(.25f,.13f,.28f);
                    var material = new Material(Shader.Find("Standard")); material.color = color; materials.Add(material); cube.GetComponent<Renderer>().sharedMaterial=material;
                    var textObject = new GameObject("Glyph " + glyph); textObject.transform.SetParent(root,false); textObject.transform.localPosition = new Vector3(0,.072f,0); textObject.transform.localRotation=Quaternion.Euler(90,0,0);
                    var text=textObject.AddComponent<TextMesh>(); text.text=glyph; text.fontSize=80; text.characterSize=.021f; text.anchor=TextAnchor.MiddleCenter; text.alignment=TextAlignment.Center; text.color=new Color(.035f,.07f,.10f); text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); textObject.GetComponent<MeshRenderer>().sharedMaterial=text.font.material;
                    components.Add(new LetterComponentView { id=id,letter=glyph,groupIndex=group,transform=root,initialPosition=position,initialRotation=rotation });
                }
            }
            ResetLayout();
        }
        public IEnumerator Assemble(int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= groups.Length || groups[groupIndex] == null) yield break;
            int generation = layoutGeneration;
            var selected = components.FindAll(c => c.groupIndex == groupIndex);
            var from = new Vector3[selected.Count]; var rotations = new Quaternion[selected.Count];
            for (int i=0;i<selected.Count;i++) { from[i]=selected[i].transform.position; rotations[i]=selected[i].transform.rotation; }
            float elapsed=0;
            while (elapsed < AssemblySeconds)
            {
                if (generation != layoutGeneration) yield break;
                elapsed += Time.unscaledDeltaTime; float t=Mathf.SmoothStep(0,1,Mathf.Clamp01(elapsed/Mathf.Max(.001f,AssemblySeconds)));
                for(int i=0;i<selected.Count;i++)
                {
                    var destination=groups[groupIndex].TransformPoint(new Vector3((i-(selected.Count-1)*.5f)*LetterSpacing,0,0));
                    selected[i].transform.position=Vector3.Lerp(from[i],destination,t)+DetectionOrigin.up*(Mathf.Sin(t*Mathf.PI)*.16f);
                    selected[i].transform.rotation=Quaternion.Slerp(rotations[i],groups[groupIndex].rotation,t);
                }
                yield return null;
            }
            if (generation != layoutGeneration) yield break;
            PlaceLettersInGroup(groupIndex);
            assembled[groupIndex]=true;
            var observation=ReadGroup(groupIndex); Observations[groupIndex]=observation;
            Recognized?.Invoke(observation);
        }
        void PlaceLettersInGroup(int index)
        {
            var selected = components.FindAll(c => c.groupIndex == index);
            for (int i = 0; i < selected.Count; i++)
            {
                selected[i].transform.SetParent(groups[index], false);
                selected[i].transform.localPosition = new Vector3((i - (selected.Count - 1) * .5f) * LetterSpacing, 0, 0);
                selected[i].transform.localRotation = Quaternion.identity;
            }
        }
        public RecognizedWordObservation ReadGroup(int index)
        {
            var selected=components.FindAll(c=>c.groupIndex==index);
            selected.Sort((a,b)=>groups[index].InverseTransformPoint(a.transform.position).x.CompareTo(groups[index].InverseTransformPoint(b.transform.position).x));
            string raw=""; var ids=new string[selected.Count]; Vector3 center=Vector3.zero;
            for(int i=0;i<selected.Count;i++) { raw+=selected[i].letter; ids[i]=selected[i].id; center+=selected[i].transform.position; }
            return new RecognizedWordObservation { groupIndex=index,groupId=GroupIds[index],rawText=raw,word=raw.Replace("#",""),isFinish=raw.EndsWith("#",StringComparison.Ordinal),componentIds=ids,worldPosition=center/selected.Count };
        }
        public void ResetLayout()
        {
            layoutGeneration++; StopAllCoroutines();
            var randomCenters = GenerateRandomGroupPositions();
            Array.Copy(randomCenters, centers, centers.Length);
            for(int i=0;i<3;i++) { assembled[i]=false; Observations[i]=null; groups[i].localPosition=centers[i]; groups[i].localRotation=Quaternion.identity; }
            foreach(var c in components) { c.transform.SetParent(DetectionOrigin,false); c.transform.localPosition=c.initialPosition; c.transform.localRotation=c.initialRotation; }
        }
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        static bool Finite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w) &&
            value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > .00001f;
        void OnDestroy() { foreach(var material in materials) if(material!=null) Destroy(material); }
    }
}
