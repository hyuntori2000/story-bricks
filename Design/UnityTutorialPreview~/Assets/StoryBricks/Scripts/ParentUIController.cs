using System.Collections.Generic;
using UnityEngine;

/// Switches the parent-side screens. The story server calls OnEvent(name);
/// Next/Prev are for testing without the server.
public class ParentUIController : MonoBehaviour
{
    [System.Serializable]
    public class Screen
    {
        public string id;
        public string[] events;
        public GameObject root;
    }

    public List<Screen> screens = new List<Screen>();
    public int current;

    void Start() { ShowIndex(current); }

    public void OnEvent(string evt)
    {
        for (int i = 0; i < screens.Count; i++)
            foreach (var e in screens[i].events)
                if (e == evt) { ShowIndex(i); return; }
        Debug.LogWarning("ParentUI: no screen for event " + evt);
    }

    public void ShowIndex(int i)
    {
        if (screens.Count == 0) return;
        current = ((i % screens.Count) + screens.Count) % screens.Count;
        for (int k = 0; k < screens.Count; k++)
            if (screens[k].root != null) screens[k].root.SetActive(k == current);
    }

    [ContextMenu("Next")] public void Next() { ShowIndex(current + 1); }
    [ContextMenu("Prev")] public void Prev() { ShowIndex(current - 1); }
    public void Restart() { ShowIndex(0); }
    public void GoToBuilding() { OnEvent("building"); }
    public void GoToStory() { OnEvent("story_start"); }
}
