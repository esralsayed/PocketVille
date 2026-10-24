using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Arcade Rigidbody driver for the tuk-tuk. Forces and yaw are applied in
/// FixedUpdate. The vehicle transform is never moved directly.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class TukTukController : MonoBehaviour
{
    [Header("Speed")]
    [Tooltip("Top forward speed in meters per second.")]
    [SerializeField] float maxForwardSpeed = 12f;

    [Tooltip("Top reverse speed in meters per second.")]
    [SerializeField] float maxReverseSpeed = 4.5f;

    [Tooltip("Acceleration while the throttle is held, in m/s^2.")]
    [SerializeField] float acceleration = 8f;

    [Tooltip("Deceleration when braking against forward motion, in m/s^2.")]
    [SerializeField] float brakeDeceleration = 18f;

    [Tooltip("Deceleration when no drive input is held, in m/s^2.")]
    [SerializeField] float coastDeceleration = 4f;

    [Tooltip("Deceleration while the handbrake is held, in m/s^2.")]
    [SerializeField] float handbrakeDeceleration = 36f;

    [Header("Steering")]
    [Tooltip("Yaw rate at full steering lock, in degrees per second.")]
    [SerializeField] float steerSpeed = 85f;

    [Tooltip("How quickly the yaw rate catches the stick, in degrees per second squared.")]
    [SerializeField] float steerResponse = 180f;

    [Tooltip("Forward speed where steering reaches full strength, in m/s.")]
    [SerializeField] float steerFullSpeed = 3.5f;

    [Tooltip("Below this speed the tuk-tuk does not yaw, in m/s.")]
    [SerializeField] float minimumSteerSpeed = 0.6f;

    [Header("Grip and stability")]
    [Tooltip("How quickly sideways slide is removed, in m/s^2.")]
    [SerializeField] float lateralGrip = 14f;

    [Tooltip("Rigidbody center of mass in local space. A lower value resists tipping.")]
    [SerializeField] Vector3 centerOfMass = new Vector3(0f, 0.35f, -0.15f);

    [Tooltip("Strength of the upright correction. Pitch and roll stay unlocked.")]
    [SerializeField] float uprightStrength = 10f;

    [Tooltip("Damping applied to pitch and roll rates.")]
    [SerializeField] float uprightDamping = 3.5f;

    Rigidbody body;
    TukTukFuelSystem fuel;
    InputActionMap actions;
    InputAction moveAction;
    InputAction handbrakeAction;
    PhysicsMaterial driveSurface;
    Vector2 moveInput;
    bool handbrakeHeld;
#if UNITY_EDITOR
    InputSettings.EditorInputBehaviorInPlayMode previousEditorInput;
    bool changedEditorInput;
#endif

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        body.centerOfMass = centerOfMass;
        fuel = GetComponent<TukTukFuelSystem>();
        // Ground friction was cancelling the small speed added each physics
        // step, so the tuk-tuk never left its spot. Grip is applied below.
        ApplyDriveSurface();

        actions = new InputActionMap("TukTuk");
        moveAction = actions.AddAction("Move", InputActionType.Value);
        BindMove("<Keyboard>/w", "<Keyboard>/s", "<Keyboard>/a", "<Keyboard>/d");
        BindMove("<Keyboard>/upArrow", "<Keyboard>/downArrow", "<Keyboard>/leftArrow", "<Keyboard>/rightArrow");
        handbrakeAction = actions.AddAction("Handbrake", InputActionType.Button, "<Keyboard>/space");
    }

    void BindMove(string up, string down, string left, string right)
    {
        moveAction.AddCompositeBinding("2DVector")
            .With("up", up)
            .With("down", down)
            .With("left", left)
            .With("right", right);
    }

    void ApplyDriveSurface()
    {
        Collider hull = GetComponent<BoxCollider>();
        if (hull == null)
            return;

        driveSurface = new PhysicsMaterial("TukTukDrive");
        driveSurface.staticFriction = 0f;
        driveSurface.dynamicFriction = 0f;
        driveSurface.bounciness = 0f;
        driveSurface.frictionCombine = PhysicsMaterialCombine.Multiply;
        driveSurface.bounceCombine = PhysicsMaterialCombine.Minimum;
        hull.sharedMaterial = driveSurface;
    }

    void OnEnable()
    {
        actions.Enable();
#if UNITY_EDITOR
        if (Application.isPlaying && InputSystem.settings != null)
        {
            previousEditorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            changedEditorInput = true;
        }
#endif
    }

    void Update()
    {
        Vector2 move = moveAction.ReadValue<Vector2>();
        bool handbrake = handbrakeAction.IsPressed();
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            float x = 0f;
            float y = 0f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                x += 1f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                x -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                y += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                y -= 1f;
            if (x != 0f || y != 0f)
                move = new Vector2(Mathf.Clamp(x, -1f, 1f), Mathf.Clamp(y, -1f, 1f));
            handbrake = handbrake || keyboard.spaceKey.isPressed;
        }

        moveInput = move;
        handbrakeHeld = handbrake;
    }

    void OnDisable()
    {
        if (actions != null)
            actions.Disable();
#if UNITY_EDITOR
        if (changedEditorInput && InputSystem.settings != null)
        {
            InputSystem.settings.editorInputBehaviorInPlayMode = previousEditorInput;
            UnityEditor.EditorUtility.ClearDirty(InputSystem.settings);
            changedEditorInput = false;
        }
#endif
    }

    void OnDestroy()
    {
        if (actions != null)
        {
            actions.Disable();
            actions.Dispose();
            actions = null;
        }

        if (driveSurface != null)
        {
            Collider hull = GetComponent<BoxCollider>();
            if (hull != null && hull.sharedMaterial == driveSurface)
                hull.sharedMaterial = null;
            Destroy(driveSurface);
            driveSurface = null;
        }
    }

    void FixedUpdate()
    {
        float throttle = Mathf.Clamp(moveInput.y, -1f, 1f);
        float steer = Mathf.Clamp(moveInput.x, -1f, 1f);
        bool handbrake = handbrakeHeld;
        if (throttle != 0f || steer != 0f || handbrake)
            body.WakeUp();

        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
            return;

        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        Vector3 velocity = body.linearVelocity;
        Vector3 flatVelocity = Vector3.ProjectOnPlane(velocity, Vector3.up);
        float forwardSpeed = Vector3.Dot(flatVelocity, forward);
        float sideSpeed = Vector3.Dot(flatVelocity, right);
        bool canPropel = fuel == null || fuel.HasFuel;

        float targetSpeed;
        float speedChangeRate;
        GetLongitudinalTarget(throttle, handbrake, forwardSpeed, canPropel, out targetSpeed, out speedChangeRate);

        float deltaTime = Time.fixedDeltaTime;
        float newForwardSpeed = Mathf.MoveTowards(forwardSpeed, targetSpeed, speedChangeRate * deltaTime);
        float grip = handbrake ? lateralGrip + handbrakeDeceleration : lateralGrip;
        float newSideSpeed = Mathf.MoveTowards(sideSpeed, 0f, grip * deltaTime);

        Vector3 flatVelocityAfter = forward * newForwardSpeed + right * newSideSpeed;
        Vector3 velocityAfter = new Vector3(flatVelocityAfter.x, velocity.y, flatVelocityAfter.z);
        body.linearVelocity = velocityAfter;
        if (fuel != null && IsEnginePropelling(throttle, handbrake, forwardSpeed, canPropel))
            fuel.ConsumePropulsion(throttle, flatVelocity.magnitude, deltaTime);

        ApplySteering(steer, newForwardSpeed, deltaTime);
        ApplyUprightTorque();
    }

    bool IsEnginePropelling(float throttle, bool handbrake, float forwardSpeed, bool canPropel)
    {
        if (handbrake || !canPropel)
            return false;
        if (throttle > 0.01f)
            return true;

        return throttle < -0.01f && forwardSpeed <= minimumSteerSpeed;
    }

    void GetLongitudinalTarget(float throttle, bool handbrake, float forwardSpeed, bool canPropel, out float targetSpeed, out float speedChangeRate)
    {
        if (handbrake)
        {
            targetSpeed = 0f;
            speedChangeRate = handbrakeDeceleration;
            return;
        }

        if (throttle > 0.01f)
        {
            if (!canPropel)
            {
                targetSpeed = 0f;
                speedChangeRate = coastDeceleration;
                return;
            }

            targetSpeed = maxForwardSpeed * throttle;
            bool opposing = forwardSpeed < -0.2f || forwardSpeed > targetSpeed;
            speedChangeRate = opposing ? brakeDeceleration : acceleration;
            return;
        }

        if (throttle < -0.01f)
        {
            if (forwardSpeed > minimumSteerSpeed)
            {
                targetSpeed = 0f;
                speedChangeRate = brakeDeceleration;
                return;
            }

            if (!canPropel)
            {
                targetSpeed = 0f;
                speedChangeRate = coastDeceleration;
                return;
            }

            targetSpeed = -maxReverseSpeed * -throttle;
            bool opposing = forwardSpeed < targetSpeed;
            speedChangeRate = opposing ? brakeDeceleration : acceleration;
            return;
        }

        targetSpeed = 0f;
        speedChangeRate = coastDeceleration;
    }

    void ApplySteering(float steer, float forwardSpeed, float deltaTime)
    {
        float direction = 0f;
        if (forwardSpeed > minimumSteerSpeed)
            direction = 1f;
        else if (forwardSpeed < -minimumSteerSpeed)
            direction = -1f;

        float speedFactor = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(0.01f, steerFullSpeed));
        float desiredYaw = steer * steerSpeed * Mathf.Deg2Rad * speedFactor * direction;

        Vector3 angular = body.angularVelocity;
        Vector3 yaw = Vector3.Project(angular, Vector3.up);
        Vector3 tilt = angular - yaw;
        float newYaw = Mathf.MoveTowards(yaw.y, desiredYaw, steerResponse * Mathf.Deg2Rad * deltaTime);
        body.angularVelocity = tilt + Vector3.up * newYaw;
    }

    void ApplyUprightTorque()
    {
        Vector3 tiltAxis = Vector3.Cross(transform.up, Vector3.up);
        Vector3 tiltRate = Vector3.ProjectOnPlane(body.angularVelocity, Vector3.up);
        body.AddTorque(tiltAxis * uprightStrength - tiltRate * uprightDamping, ForceMode.Acceleration);
    }
}
