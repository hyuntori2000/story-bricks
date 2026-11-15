using System;
using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>Coordinates are metres relative to BlockInputHub.detectionOrigin, +Y up, +Z forward.</summary>
    [Serializable]
    public sealed class BlockObservation
    {
        public string id;
        public string word;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public bool visible = true;
        public BlockObservation Copy() => new BlockObservation { id = id, word = word, position = position, rotation = rotation, visible = visible };
    }
    [Serializable]
    public sealed class BlockInputMessage
    {
        public string operation = "upsert";
        public string id;
        public string word;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public bool visible = true;
    }
}
