using UnityEngine;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Optional end signal for an authored animation or Timeline callback.</summary>
    public sealed class JudyCueSignal : MonoBehaviour
    {
        public bool Completed { get; private set; }
        public void Complete() { Completed = true; }
    }
}
