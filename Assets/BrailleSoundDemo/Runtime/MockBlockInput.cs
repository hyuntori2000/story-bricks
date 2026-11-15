using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>The only mock producer. Disable this component and use the hub for a real recognizer.</summary>
    public sealed class MockBlockInput : MonoBehaviour
    {
        public BlockInputHub hub;
        public void Seed()
        {
            if (!enabled || hub == null) return;
            hub.Submit(new BlockObservation { id = "block-cat", word = "CAT", position = new Vector3(-.65f, .08f, 0) });
            hub.Submit(new BlockObservation { id = "block-dog", word = "DOG", position = new Vector3(0, .08f, .12f) });
            hub.Submit(new BlockObservation { id = "block-rain", word = "RAIN", position = new Vector3(.65f, .08f, 0) });
        }
        public void Move(string id, Vector3 delta)
        {
            if (!enabled || hub == null || !hub.TryGet(id, out var value)) return;
            value.position += delta; hub.Submit(value);
        }
        public void Touch(string id) { if (enabled) hub.Activate(id); }
    }
}
