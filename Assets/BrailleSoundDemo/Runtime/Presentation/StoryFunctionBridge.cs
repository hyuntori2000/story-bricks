using BrailleSoundDemo.PhysicalStory;
using UnityEngine;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>UI-independent public functions. Bind your own prefab buttons through the Inspector.</summary>
    public sealed class StoryFunctionBridge : MonoBehaviour
    {
        [SerializeField] PhysicalStoryCoordinator coordinator;
        PhysicalStoryCoordinator Core
        {
            get
            {
                if(coordinator==null)coordinator=FindFirstObjectByType<PhysicalStoryCoordinator>();
                return coordinator;
            }
        }
        public void UseLiveDemo() { var router=Core?.GetComponent<ExperienceRouter>(); if(router!=null) router.SelectCreateWorld(); else Core?.ConfigureWorkspace("demo",true); }
        public void UseJudyStory()=>Core?.GetComponent<ExperienceRouter>()?.SelectJudy();
        public void ReturnToMenu()=>Core?.GetComponent<ExperienceRouter>()?.ReturnToMenu();
        public void GoHome()=>ReturnToMenu();
        public void ReplayStory()=>Core?.GetComponent<ExperienceRouter>()?.Replay();
        public void SeePreviousStories()=>Core?.GetComponent<ExperienceRouter>()?.SelectPreviousStories();
        public void PlayCue(string cueKey)=>Core?.EmitPresentationEvent("manual",null,cueKey);
        public void EmitEvent(string eventName)=>Core?.EmitPresentationEvent(eventName);
        public void StartNextStory()=>Core?.StartNextConversation();
        public void OpenFinishMenu()=>Core?.GetComponent<SessionFinishMenu>()?.Open();
        public void CreateStory()=>Core?.CreateStoryFromSavedProgress();
        public void EndSession()=>Core?.EndSessionToStart();
        public void ResetSession() { var router=Core?.GetComponent<ExperienceRouter>(); if(router!=null) router.RestartSelectedExperience(); else Core?.ResetPhysicalSession("Reset requested by the presentation UI"); }
    }
}
