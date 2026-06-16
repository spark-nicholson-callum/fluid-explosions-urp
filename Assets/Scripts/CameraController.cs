using UnityEngine;

public class CameraController : MonoBehaviour
{
    private InputSystem_Actions input;
    private Vector2 moveInput;
    private Vector2 viewInput;

    [SerializeField] private Transform playerTransform;

    [Header("Settings")]
    [SerializeField] private float mouseSensitivity = 0.15f;
    [SerializeField] private float moveSpeed = 10f;

    private float xRotation = 0f;

    public void Awake()
    {
        input = new InputSystem_Actions();

        input.Player.Move.performed += ctx => moveInput = ctx.ReadValue<Vector2>();
        input.Player.Move.canceled += ctx => moveInput = Vector2.zero;

        input.Player.Look.performed += ctx => viewInput = ctx.ReadValue<Vector2>();
        input.Player.Look.canceled += ctx => viewInput = Vector2.zero;

        xRotation = transform.eulerAngles.x;
    }

    public void OnEnable()
    {
        input.Enable();
    }

    public void OnDisable()
    {
        input.Disable();
    }

    public void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public void Update()
    {
        // Look Controls
        float viewX = viewInput.x * mouseSensitivity;
        float viewY = viewInput.y * mouseSensitivity;

        xRotation -= viewY;
        xRotation = Mathf.Clamp(xRotation , -90f, 90f);

        transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        playerTransform.Rotate(Vector3.up * viewX);

        // Move Controls
        Vector3 move = (transform.forward * moveInput.y) + (transform.right * moveInput.x);
        playerTransform.position += move * moveSpeed * Time.deltaTime;
    }
}
