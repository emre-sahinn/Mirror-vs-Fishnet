using FishNet.Object;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FN
{
    public class SimulatePlayer : NetworkBehaviour
    {
        private float _nextMoveUpdate = 0f;
        private Vector3 _posGoal;
        private Quaternion _rotGoal;

        private void Update()
        {
            if (!base.IsOwner)
                return;

            if (Time.time > _nextMoveUpdate)
            {
                _nextMoveUpdate = Time.time + .05f;

                bool rotate = (Random.Range(0f, 1f) <= 0.5f);
                bool x = (Random.Range(0f, 1f) <= 0.5f);
                bool y = (Random.Range(0f, 1f) <= 0.5f);
                bool z = (Random.Range(0f, 1f) <= 0.5f);

                if (!x && !y && !z)
                    x = true;

                float xPos = (x) ? Random.Range(-30f, 30f) : _posGoal.x;
                float yPos = (y) ? Random.Range(-30f, 30f) : _posGoal.y;
                float zPos = (z) ? Random.Range(-30f, 30f) : _posGoal.z;

                _posGoal = new Vector3(xPos, yPos, zPos);
                if (rotate)
                    _rotGoal = Quaternion.Euler(Random.Range(-80f, 80f),
                        Random.Range(-80f, 80f),
                        Random.Range(-80f, 80f)
                        );

                ServerRpc(_posGoal, _rotGoal);
            }
        }

        [ServerRpc]
        private void ServerRpc(Vector3 position, Quaternion rotation)
        {
            ObserversRpc(position, rotation);
        }

        [ObserversRpc]
        private void ObserversRpc(Vector3 position, Quaternion rotation)
        {
            transform.position = position;
            transform.rotation = rotation;
        }
    }
}