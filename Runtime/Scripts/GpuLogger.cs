using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace CallumNicholson.FluidExplosionURP
{
    public class GpuLogger<T> : MonoBehaviour where T : Enum
    {
        private class SampleRecordPair
        {
            public readonly CustomSampler sampler;
            public readonly Recorder recorder;

            public SampleRecordPair(string name)
            {
                sampler = CustomSampler.Create(name, true);
                recorder = sampler.GetRecorder();
                recorder.enabled = true;
            }
        }

        [SerializeField] private TextMeshProUGUI outText;
        [SerializeField] private T[] ignore;

        private Dictionary<T, SampleRecordPair> recorders;

        public void Start()
        {
            recorders = Enum.GetValues(typeof(T))
                .Cast<T>()
                .Where(stage => !ignore.Contains(stage))
                .ToDictionary(
                    stage => stage,
                    stage => new SampleRecordPair(stage.ToString())
                );
        }

        public void Update()
        {
            if (outText == null) return;

            StringBuilder builder = new();
            var stages = Enum.GetValues(typeof(T))
                .Cast<T>()
                .Where(stage => !ignore.Contains(stage));
            foreach (T step in stages)
            {
                SampleRecordPair recorder = recorders[step];
                float timeMs = recorder.recorder.gpuElapsedNanoseconds / 1000000f;
                if (timeMs < 0 || timeMs > 50)
                {
                    builder.AppendLine($"{step.ToString()}: --- ms");
                }
                else
                {
                    builder.AppendLine($"{step.ToString()}: {timeMs:F3} ms");
                }
            }
            outText.text = builder.ToString();
        }

        public void Begin(T stage, CommandBuffer cmd)
        {
            cmd.BeginSample(recorders[stage].sampler);
        }

        public void End(T stage, CommandBuffer cmd)
        {
            cmd.EndSample(recorders[stage].sampler);
        }
    }
}
