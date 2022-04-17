using Mirror;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AutoStart : MonoBehaviour
{
    public NetworkManager netManager;
    // Start is called before the first frame update
    void Start()
    {
#if SERVER_BUILD
        netManager.StartServer();
#else
        netManager.StartClient();
#endif
    }
}
