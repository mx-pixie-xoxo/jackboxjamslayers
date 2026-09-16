using UnityEngine;
public class TitleAnimations : MonoBehaviour
{
    public Animator animator;
    public float idleOffset = 0.1f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        animator.SetFloat("Idle_Offset", idleOffset);
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
