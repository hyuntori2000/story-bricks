using UnityEngine;

namespace BrailleSoundDemo.PhysicalStory
{
    /// <summary>The generated effect belongs to the small, fixed word marker.</summary>
    [DisallowMultipleComponent]
    public sealed class WordSoundAttachment : MonoBehaviour
    {
        public AudioSource Source { get; private set; }
        public string GroupId { get; private set; }
        public string Word { get; private set; }
        LineRenderer outline;
        Vector3[] restingCorners, animatedCorners;
        readonly float[] audioSamples=new float[128];
        float restingStartWidth, restingEndWidth, activity, energy, phase;
        bool visualPlaying, visualChanged;
        public void Configure(string groupId,string word)
        {
            GroupId=groupId;Word=word;
            if(Source==null) Source=gameObject.AddComponent<AudioSource>();
            Source.playOnAwake=false;Source.loop=false;Source.spatialBlend=1;
            Source.dopplerLevel=0;Source.rolloffMode=AudioRolloffMode.Linear;
            Source.minDistance=.4f;Source.maxDistance=8;Source.volume=.8f;
            bool configured=AudioSettings.GetSpatializerPluginName()=="Meta XR Audio";
            Source.spatialize=configured;
            var spatial=GetComponent<MetaXRAudioSource>();
            if(spatial==null) spatial=gameObject.AddComponent<MetaXRAudioSource>();
            spatial.EnableSpatialization=configured;spatial.EnableAcoustics=false;
            RestoreOutline();
            outline=GetComponent<LineRenderer>();
            if(outline!=null && !outline.useWorldSpace && outline.positionCount>=3)
            {
                restingCorners=new Vector3[outline.positionCount];
                animatedCorners=new Vector3[outline.positionCount];
                outline.GetPositions(restingCorners);
                restingStartWidth=outline.startWidth;restingEndWidth=outline.endWidth;
            }
        }
        public void Attach(AudioClip clip)
        {
            if(Source==null) return;
            Source.Stop();Source.clip=clip;
            Debug.Log("[Word box sound] attached word="+Word+" seconds="+(clip!=null?clip.length:0).ToString("F2"));
        }

        void LateUpdate()
        {
            if(outline==null || restingCorners==null || outline.positionCount!=restingCorners.Length) return;
            bool playing=Source!=null && Source.isPlaying && Source.clip!=null && !Source.mute && Source.volume>0;
            if(playing!=visualPlaying)
            {
                visualPlaying=playing;
                Debug.Log("[Word box sound] "+(playing ? "playing; wobble on" : "stopped; wobble settling")+" word="+Word);
            }
            float dt=Mathf.Min(Time.unscaledDeltaTime,.1f);
            activity=Mathf.MoveTowards(activity,playing ? 1f : 0f,dt*(playing ? 6f : 4f));
            if(activity<=0f)
            {
                if(visualChanged) RestoreOutline();
                return;
            }
            if(playing)
            {
                Source.GetOutputData(audioSamples,0);
                float sum=0f;
                foreach(float sample in audioSamples) sum+=sample*sample;
                float level=Mathf.Clamp01(Mathf.Sqrt(sum/audioSamples.Length)*5f);
                energy=Mathf.Lerp(energy,level,1f-Mathf.Exp(-12f*dt));
                phase=Source.time*Mathf.PI*3.6f;
            }
            float wobble=Mathf.Sin(phase)*activity*(.10f+.10f*energy);
            for(int i=0;i<restingCorners.Length;i++)
            {
                Vector3 corner=restingCorners[i];
                corner.x*=1f+wobble;
                corner.z*=1f-wobble*.65f;
                corner.y+=Mathf.Sin(phase+i*Mathf.PI*.5f)*.0025f*activity;
                animatedCorners[i]=corner;
            }
            // Animate only local line geometry. The frozen anchor, recognition
            // coordinates, and spatial AudioSource remain completely stationary.
            outline.SetPositions(animatedCorners);
            float width=1f+activity*(.15f+.15f*Mathf.Sin(phase));
            outline.startWidth=restingStartWidth*width;
            outline.endWidth=restingEndWidth*width;
            visualChanged=true;
        }

        void RestoreOutline()
        {
            if(outline!=null && restingCorners!=null && outline.positionCount==restingCorners.Length)
            {
                outline.SetPositions(restingCorners);
                outline.startWidth=restingStartWidth;outline.endWidth=restingEndWidth;
            }
            activity=energy=phase=0f;visualPlaying=false;visualChanged=false;
        }

        void OnDisable() { if(Source!=null) Source.Stop();RestoreOutline(); }
    }
}
