using Mirror;
using UnityEngine;

public class DisplayPerformance : MonoBehaviour
{
    public int TargetFrameRate = 60;
    private float _nextDisplayTime = 0f;
    private MovingAverage _average = new MovingAverage(3);

    private uint _frames = 0;

    private void Update()
    {
#if SERVER_BUILD
        Application.targetFrameRate = TargetFrameRate;

        _frames++;
        if (Time.time < _nextDisplayTime)
            return;

        _average.ComputeAverage(_frames);
        _frames = 0;
        //Update display twice a second.
        _nextDisplayTime = Time.time + 1f;

        double avgFrameRate = _average.Average;
        //Performance lost.
        double lost = avgFrameRate / (double)TargetFrameRate;
        lost = (1d - lost);

        //Replace this with the equivelent of your networking solution.
        int clientCount = NetworkServer.connections.Count;//InstanceFinder.ServerManager.Clients.Count;

        Debug.Log($"Average {lost.ToString("0.###")} performance lost with {clientCount} clients.");
#elif UNITY_EDITOR
        //Max out editor frames to test client side scalability.
        Application.targetFrameRate = 9999;
#else
        /* Limit client frame rate to 15
         * so your computer doesn't catch fire when opening
         * hundreds of clients. */
        Application.targetFrameRate = 15;
#endif
    }
}