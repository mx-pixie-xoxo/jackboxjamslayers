using UnityEngine;
using PurrNet;
using UnityEngine.InputSystem;

public class PlayerPhysics : NetworkBehaviour
{
    [SerializeField] private Rigidbody rb;
    [SerializeField] private float maxSpeed = 5;
    [SerializeField] private float moveForce = 100;
    private float moveInput;


    protected override void OnSpawned(bool asServer)
    {
        base.OnSpawned(asServer);
        if (asServer)
            return;

        if (!isServer)
        {
            rb.isKinematic = true;
        }

        if (isOwner)
            networkManager.onTick += OnTick;
    }

    private void OnTick(bool asServer)
    {
        if (asServer)
            return;

        rb.linearVelocity = new Vector2(moveInput*moveForce, rb.linearVelocity.y);
    }

    [ServerRpc]
    private void OnMove(InputValue value)
    {
        moveInput = value.Get<Vector2>().x;
        Debug.Log(value.ToString());
    }

    void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
