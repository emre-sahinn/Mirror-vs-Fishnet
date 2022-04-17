using FishNet.Managing;
using FishNet.Managing.Client;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FN
{
    public class AutoStart : MonoBehaviour
    {
        public NetworkManager netManager;
        // Start is called before the first frame update
        void Start()
        {
#if SERVER_BUILD
        netManager.ServerManager.StartConnection();
#else
        netManager.ClientManager.StartConnection();
#endif
        }
    }
}
