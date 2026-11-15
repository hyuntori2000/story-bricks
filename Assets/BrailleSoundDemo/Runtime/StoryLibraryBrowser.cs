using System;
using System.Collections;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace BrailleSoundDemo
{
    [Serializable]
    public sealed class StoryLibraryItem
    {
        public string id,workspaceId,experienceSessionId,sessionId,title,createdAtUtc,videoPath;
        public string[] words;
        public float durationSeconds;
        public bool Valid => workspaceId=="demo" && ValidId(id) && ValidId(experienceSessionId) && ValidId(sessionId) &&
            videoPath=="/story-videos/"+id+".mp4" && !string.IsNullOrWhiteSpace(title) && title.Length<=100;
        static bool ValidId(string value)=>Guid.TryParseExact(value,"N",out var parsed) && parsed.ToString("N")==value;
    }

    /// <summary>Read-only Mac movie library. Never creates a conversation or generation request.</summary>
    public sealed class StoryLibraryBrowser : MonoBehaviour
    {
        [Serializable] sealed class Response { public int schemaVersion; public StoryLibraryItem[] stories; }
        const int PageSize=3,MaximumBytes=512*1024;
        ViewerFlow view;
        CanvasGroup panel;
        TMP_Text heading,message;
        readonly Button[] rows=new Button[PageSize];
        Button previous,next;
        StoryLibraryItem[] items=Array.Empty<StoryLibraryItem>();
        UnityWebRequest pending;
        int page,generation;
        public event Action<StoryLibraryItem> Selected;
        public event Action BackRequested;

        public void Configure(ViewerFlow presentation) { view=presentation; }
        public void Open()
        {
            Close();EnsurePanel();
            panel.gameObject.SetActive(true);panel.alpha=1;panel.interactable=true;panel.blocksRaycasts=true;
            items=Array.Empty<StoryLibraryItem>();page=0;
            ShowPage("Loading saved stories from the Mac...");
            StartCoroutine(Load(generation));
        }
        public void Hide() { if(panel!=null) panel.gameObject.SetActive(false); }
        public void ShowPlaybackError(string message)
        {
            Close();EnsurePanel();panel.gameObject.SetActive(true);panel.alpha=1;
            items=Array.Empty<StoryLibraryItem>();page=0;ShowPage(message);
        }
        public void Close()
        {
            ++generation;
            pending?.Abort();StopAllCoroutines();pending?.Dispose();pending=null;
            Hide();
        }
        IEnumerator Load(int token)
        {
            using(var request=UnityWebRequest.Get(LiveStoryProvider.BaseUrl+"/story-library"))
            {
                pending=request;request.timeout=15;request.redirectLimit=0;
                var operation=request.SendWebRequest();
                while(!operation.isDone && token==generation)
                {
                    if(request.downloadedBytes>MaximumBytes) { request.Abort();break; }
                    yield return null;
                }
                if(token!=generation) yield break;
                pending=null;
                Response result=null;
                if(request.result==UnityWebRequest.Result.Success && request.downloadedBytes<=MaximumBytes)
                    try { result=JsonUtility.FromJson<Response>(request.downloadHandler.text); } catch(Exception) { }
                if(result==null || result.schemaVersion!=1 || result.stories==null || result.stories.Length>128)
                { ShowPage("Connect the Mac, then reopen Previous stories to load your saved animations.");yield break; }
                var valid=new System.Collections.Generic.List<StoryLibraryItem>();
                foreach(var item in result.stories) if(item!=null && item.Valid) valid.Add(item);
                items=valid.ToArray();ShowPage(items.Length==0 ? "No finished animations yet. Create a story and its video will appear here." : "");
            }
        }
        void EnsurePanel()
        {
            if(panel!=null) return;
            panel=Instantiate(view.menu,view.menu.transform.parent,false);
            panel.name="Previous stories";
            var rect=(RectTransform)panel.transform;rect.sizeDelta=new Vector2(340,350);rect.anchoredPosition=Vector2.zero;
            foreach(var button in panel.GetComponentsInChildren<Button>(true)) { button.gameObject.SetActive(false);Destroy(button.gameObject); }
            foreach(var text in panel.GetComponentsInChildren<TMP_Text>(true))
                if(text.transform.parent==panel.transform) { heading=text;break; }
            heading.text="Previous stories";heading.rectTransform.anchoredPosition=new Vector2(0,148);
            message=Instantiate(heading,panel.transform,false);message.name="Library message";
            message.rectTransform.anchoredPosition=new Vector2(0,20);message.rectTransform.sizeDelta=new Vector2(290,150);
            message.fontSize=14;message.alignment=TextAlignmentOptions.Center;message.enableWordWrapping=true;
            for(int i=0;i<PageSize;i++)
            {
                int row=i;
                rows[i]=CopyButton(view.newStoryButton,"Saved story "+i,new Vector2(0,91-i*68),new Vector2(300,58));
                rows[i].onClick.AddListener(()=>Choose(row));
            }
            previous=Footer("Previous",-104,()=>{ if(page>0) { --page;ShowPage(""); } });
            Footer("Menu",0,()=>BackRequested?.Invoke());
            next=Footer("Next",104,()=>{ if((page+1)*PageSize<items.Length) { ++page;ShowPage(""); } });
        }
        Button CopyButton(Button template,string name,Vector2 position,Vector2 size)
        {
            var button=Instantiate(template,panel.transform,false);button.name=name;button.gameObject.SetActive(true);
            var rect=(RectTransform)button.transform;rect.anchorMin=rect.anchorMax=new Vector2(.5f,.5f);
            rect.anchoredPosition=position;rect.sizeDelta=size;rect.localScale=Vector3.one;
            button.onClick=new Button.ButtonClickedEvent();button.interactable=true;
            var group=button.GetComponent<CanvasGroup>();if(group!=null) { group.alpha=1;group.interactable=true;group.blocksRaycasts=true; }
            return button;
        }
        Button Footer(string label,float x,UnityEngine.Events.UnityAction click)
        {
            var button=CopyButton(view.newStoryButton,label,new Vector2(x,-128),new Vector2(96,35));
            foreach(var text in button.GetComponentsInChildren<TMP_Text>(true))
            {
                text.gameObject.SetActive(text.name=="Title");
                if(text.name!="Title") continue;
                text.text=label;text.rectTransform.anchoredPosition=Vector2.zero;text.rectTransform.sizeDelta=new Vector2(84,26);
                text.enableAutoSizing=true;text.fontSizeMax=13;text.fontSizeMin=10;text.alignment=TextAlignmentOptions.Center;
            }
            button.onClick.AddListener(click);return button;
        }
        void Choose(int row)
        {
            int index=page*PageSize+row;
            if(index<0 || index>=items.Length) return;
            Hide();Selected?.Invoke(items[index]);
        }
        void ShowPage(string notice)
        {
            message.text=notice;message.gameObject.SetActive(!string.IsNullOrEmpty(notice));
            heading.text=items.Length>PageSize ? "Previous stories  "+(page+1)+"/"+((items.Length+PageSize-1)/PageSize) : "Previous stories";
            for(int i=0;i<PageSize;i++)
            {
                int index=page*PageSize+i;rows[i].gameObject.SetActive(index<items.Length);
                if(index>=items.Length) continue;
                var item=items[index];
                string date=DateTimeOffset.TryParse(item.createdAtUtc,CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)
                    ? parsed.ToLocalTime().ToString("MMM d, yyyy  h:mm tt",CultureInfo.InvariantCulture) : "Saved animation";
                foreach(var text in rows[i].GetComponentsInChildren<TMP_Text>(true))
                    if(text.name=="Title")
                    {
                        text.text=item.title;text.enableAutoSizing=true;text.fontSizeMin=10;text.fontSizeMax=15;
                        text.enableWordWrapping=false;text.overflowMode=TextOverflowModes.Ellipsis;
                    }
                    else if(text.name=="Sub") text.text=date;
            }
            previous.interactable=page>0;next.interactable=(page+1)*PageSize<items.Length;
        }
        void OnDisable()=>Close();
    }
}
