using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public class TriggerButton : MonoBehaviour
{
    [SerializeField] private Transform primary;
    [SerializeField] private Transform secondaryParent;
    [SerializeField] private Button button;
    [SerializeField] private float blurTime;
    [SerializeField] private float blurDelay;
    [SerializeField] private AnimationCurve pulseCurve = AnimationCurve.Linear(0, 0, 1, 1);

    private Coroutine pulseCoroutine;

    public void Start()
    {
        button.onClick.AddListener(() => TriggerParticles());
    }

    private void TriggerParticles()
    {
        var allMarkers = secondaryParent
            .Cast<Transform>()
            .Append(primary);

        foreach (Transform child in allMarkers)
        {
            Array.ForEach(child.GetComponentsInChildren<ParticleSystem>(), p => p.Play());
        }

        PulseBlur();
    }

    private void PulseBlur()
    {
        var primaryEffect = primary
            .Cast<Transform>()
            .FirstOrDefault(x => x.gameObject.activeSelf);
        if (primaryEffect == null) return;

        var blurMarker = primaryEffect.Find("BlurMarker");
        if (blurMarker == null) return;

        TargetPointBlurFeature.TargetPosition = blurMarker.position;

        if (pulseCoroutine != null)
        {
            StopCoroutine(pulseCoroutine);
        }
        pulseCoroutine = StartCoroutine(PulseRoutine());
    }

    private IEnumerator PulseRoutine()
    {
        yield return new WaitForSeconds(blurDelay);
        TargetPointBlurFeature.IsEffectActive = true;

        float elapsedTime = 0f;

        while (elapsedTime < blurTime)
        {
            float progress = elapsedTime / blurTime;
            float curveValue = pulseCurve.Evaluate(progress);
            TargetPointBlurFeature.BlurStrength = curveValue;

            elapsedTime += Time.deltaTime;
            yield return null;
        }
        TargetPointBlurFeature.IsEffectActive = false;

        pulseCoroutine = null;
    }
}
