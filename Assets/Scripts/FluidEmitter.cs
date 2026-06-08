using UnityEngine;

public struct EmitterData
{
    public Matrix4x4 worldToLocal;  // 64 bytes
    public int shapeType;           // 4 bytes
    public float heat;              // 4 bytes
    public float density;           // 4 bytes
    public float expansion;         // 4 bytes
    // = 80 bytes = 5 * 16 bytes
}

public class FluidEmitter : MonoBehaviour
{
    public enum Shape
    {
        Sphere,
        Box,
    }

    [Header("Shape")]
    [SerializeField] private Shape shape;

    [Header("Emission Data")]
    [SerializeField] private float density = 1.0f;
    [SerializeField] private float heat = 1.0f;
    [SerializeField] private float expansion = 0.0f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmo = true;
    [SerializeField] private Color gizmoColor = new Color(0.1215686f, 0.7058824f, 0.7215686f, 0.5f);

    public const int DataSize = 80;

    public EmitterData GetEmitterData()
    {
        return new EmitterData
        {
            worldToLocal = transform.worldToLocalMatrix,
            shapeType = (int)shape,
            density = density,
            heat = heat,
            expansion = expansion,
        };
    }

    void OnDrawGizmos()
    {
        if (!drawGizmo) return;

        Gizmos.color = gizmoColor;
        Gizmos.matrix = transform.localToWorldMatrix;
        switch (shape) {
            case Shape.Sphere:
                Gizmos.DrawSphere(Vector3.zero, 0.5f);
                break;
            case Shape.Box:
                Gizmos.DrawCube(Vector3.zero, Vector3.one);
                break;
            // No default, just do nothing
        }
    }
}
