using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using UnityEngine;
using uPLibrary.Networking.M2Mqtt.Messages;
using VaroniaBackOffice;

namespace VBO_Ultimate.Runtime.Scripts.Input
{
    public class MQTT_Weapon : _Weapon
    {
        private bool _needsUpdate = false;
        Coroutine coroutine_;
        private string MacAdress;
        private int _weaponIndex;
        
        public bool ForceConnected = false;
        

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        public IEnumerator Start()
        {

            if (ForceConnected)
                IsConnected = true;
            
            var tracking = GetComponentInParent<VaroniaWeaponTracking>();
            _weaponIndex = tracking != null ? tracking.weaponIndex : 0;

            yield return new WaitUntil(() => BackOfficeVaronia.Instance.config != null);
            yield return new WaitUntil(() => BackOfficeVaronia.Instance.mqttClient != null);
            yield return new WaitUntil(() => BackOfficeVaronia.Instance.mqttClient.client != null);

            MacAdress = BackOfficeVaronia.Instance.GetConfigField<string>("WeaponMAC");

            Debug.Log("Subscribe to: " + MacAdress);

            BackOfficeVaronia.Instance.mqttClient.client.Subscribe(
                new string[] { "DeviceToUnity/" + MacAdress + "/#" },
                new byte[] { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });
            BackOfficeVaronia.Instance.mqttClient.ReceiveMsg += event_;
        }

        // Check connection time out 
        public IEnumerator CheckCo()
        {
            IsConnected = true;
            _needsUpdate = true;
            yield return new WaitForSeconds(5f);
            IsConnected = false;
            _needsUpdate = true;
        }

        public void event_(string title, byte[] value)
        {
            string stringvalue = System.Text.Encoding.UTF8.GetString(value);

            if (title.StartsWith("DeviceToUnity"))
                title = title.Split('/').LastOrDefault();
            else
                return;

            _mainThreadQueue.Enqueue(() =>
            {
                try
                {
                    if (!ForceConnected)
                    {
                        if (coroutine_ != null)
                            StopCoroutine(coroutine_);
                        coroutine_ = StartCoroutine(CheckCo());
                    }

                    if (title == "BAT") // Receive battery info
                    {
                        float MAX = 4.2f;
                        float MIN = 3.15f;
                        float val = (float)Math.Round(float.Parse(stringvalue), 1);
                        BatteryLevel = (float)Math.Round(((val - MIN) / (MAX - MIN) * 100), 0);
                        _needsUpdate = true;
                    }

                    if (title == "BOOT_TIME") // The time the weapon is active, in seconds.
                    {
                        BOOT_Time = long.Parse(stringvalue) / 1000;
                        _needsUpdate = true;
                    }

                    if (title == "RSSI") //Received Signal Strength Indicator
                    {
                        RSSI = float.Parse(stringvalue);
                        _needsUpdate = true;
                    }

                    if (!DebugModeOverlay.IsDebugMode)
                    {
                        // Trigger
                        if (title == "1")
                        {
                            if (stringvalue == "1") VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Primary, true);
                            if (stringvalue == "0") VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Primary, false);
                        }

                        // Front side buttons
                        if (title == "2")
                        {
                            if (stringvalue == "1") VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Secondary, true);
                            if (stringvalue == "0") VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Secondary, false);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MQTT_Weapon] Error processing message: {e.Message}");
                }
            });
        }

        void Update()
        {
            while (_mainThreadQueue.TryDequeue(out Action action))
                action?.Invoke();

            if (_needsUpdate)
            {
                VaroniaBackOffice.VaroniaInput.SetDeviceData(
                    _weaponIndex,
                    IsConnected,
                    (int)BatteryLevel,
                    (float)RSSI,
                    (int)BOOT_Time,
                    WeaponInfo.DisplayNameModel);
                _needsUpdate = false;
            }
        }
    }
}
