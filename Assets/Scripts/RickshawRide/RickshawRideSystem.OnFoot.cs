using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Fall-zone rescue, walking on foot and the follow camera.
/// </summary>
public partial class RickshawRideSystem
{
    // ---------- fall zone ----------
    // If a vehicle (or the player on foot) falls far below the roads, put it back on the road at the last
    // spot where it was safely on the ground.
    [Header("Fall zone")]
    [Tooltip("Scene object (by name) whose lowest point marks the road level.")]
    [SerializeField] string fallReferenceName = "Roads & Infrastructure";
    [Tooltip("Falling this far below the lowest road triggers the rescue (meters).")]
    [SerializeField] float fallDepth = 25f;

    float fallY = float.NaN;
    Vector3 driverSafePos;
    float nextDriverSafeTime;

    void ComputeFallLevel()
    {
        float lowest = float.MaxValue;
        GameObject reference = GameObject.Find(fallReferenceName);
        if (reference != null)
            foreach (Renderer r in reference.GetComponentsInChildren<Renderer>())
                lowest = Mathf.Min(lowest, r.bounds.min.y);
        if (lowest == float.MaxValue)
            lowest = vehicles[0].Transform.position.y;
        fallY = lowest - fallDepth;
    }

    void CheckFalls()
    {
        if (vehicles.Count == 0) return;
        if (float.IsNaN(fallY)) ComputeFallLevel();

        foreach (RideVehicle v in vehicles)
        {
            Transform vt = v.Transform;
            if (vt == null || v.body == null) continue;
            Vector3 pos = v.body.position;

            if (pos.y < fallY)
            {
                // rescue: back to the last safe spot, stopped
                v.body.linearVelocity = Vector3.zero;
                v.body.angularVelocity = Vector3.zero;
                v.body.position = v.lastSafePos;
                v.body.rotation = v.lastSafeRot;
                Physics.SyncTransforms();
                v.nextSafeTime = Time.time + 1f;
                if (v == current) Toast("You fell off the map - back on the road!");
                continue;
            }

            // remember where this vehicle was last safely on the road
            if (Time.time >= v.nextSafeTime && vt.up.y > 0.7f)
            {
                v.nextSafeTime = Time.time + 0.5f;
                RaycastHit hit;
                if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out hit, 2.5f, ~0, QueryTriggerInteraction.Ignore)
                    && hit.transform.root != vt.root && !hit.transform.IsChildOf(transform))
                {
                    v.lastSafePos = pos + Vector3.up * 0.3f;
                    v.lastSafeRot = Quaternion.LookRotation(FlatForward(vt));
                }
            }
        }

        // the player on foot
        if (current == null && driver != null)
        {
            if (driver.transform.position.y < fallY)
            {
                driverController.enabled = false;
                driver.transform.position = driverSafePos;
                driverController.enabled = true;
                Toast("You fell off the map - back on the road!");
            }
            else if (Time.time >= nextDriverSafeTime && driverController.enabled && driverController.isGrounded)
            {
                nextDriverSafeTime = Time.time + 0.5f;
                driverSafePos = driver.transform.position;
            }
        }
    }

    // ---------- on foot ----------

    void Walk(Keyboard kb)
    {
        float x = 0f, y = 0f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;

        Vector3 dir = Quaternion.Euler(0f, camYaw, 0f) * new Vector3(x, 0f, y);
        if (dir.sqrMagnitude > 1f) dir.Normalize();

        bool moving = dir.sqrMagnitude > 0.01f && driverController.enabled;
        if (driverController.enabled)
            driverController.SimpleMove(dir * walkSpeed);

        if (moving)
        {
            Quaternion target = Quaternion.LookRotation(dir);
            driver.transform.rotation = Quaternion.Slerp(driver.transform.rotation, target, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime));
        }

        SetWalking(moving);
    }

    void SetWalking(bool moving)
    {
        if (driverAnimator == null)
            return;
        driverAnimator.SetBool(WalkingHash, moving);
        if (!moving)
        {
            driverAnimator.speed = 1f;
            return;
        }
        float rate = walkSpeed / WalkClipMetersPerSecond;
        if (rate < 0.6f) rate = 0.6f;
        if (rate > 3.2f) rate = 3.2f;
        driverAnimator.speed = rate;
    }

    // ---------- camera ----------

    void UpdateCamera()
    {
        if (rideCamera == null) return;
        float dt = Time.deltaTime;

        Transform focus = current != null ? current.Transform : driver.transform;
        if (current != null)
        {
            Vector3 f = FlatForward(current.Transform);
            float targetYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            camYaw = cameraSnapped ? Mathf.LerpAngle(camYaw, targetYaw, 1f - Mathf.Exp(-3f * dt)) : targetYaw;
        }
        else if (Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            camYaw += Mouse.current.delta.ReadValue().x * orbitSensitivity;
        }

        Vector3 pivot = focus.position + Vector3.up * (current != null ? 1.4f : 1.5f);
        Quaternion rot = Quaternion.Euler(cameraPitch, camYaw, 0f);
        Vector3 desired = pivot - rot * Vector3.forward * cameraDistance;

        Transform ct = rideCamera.transform;
        if (!cameraSnapped)
        {
            ct.position = desired;
            cameraSnapped = true;
        }
        else
        {
            ct.position = Vector3.Lerp(ct.position, desired, 1f - Mathf.Exp(-8f * dt));
        }
        ct.rotation = Quaternion.LookRotation(pivot - ct.position);
    }
}
