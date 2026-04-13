using Newtonsoft.Json;
using UnityEngine;

namespace VaroniaBackOffice
{
    
    
    public enum Controller
    {
        Unknown = -1,
        PICO_VSVR_CTRL = 6, 
        FOCUS3_VBS_VaroniaGun = 3, 
        FOCUS3_VBS_Striker = 50,
        FOCUS3_VBS_HK416 = 101,
        PICO_VSVR_VaroniaGun = 70,
        PICO_VSVR_Striker = 80,
        PICO_VSVR_HK416 = 416,
        VORTEX_WEAPON_FOCUS = 501,
    }
    
    
    
    /// <summary>
    /// Represents the global configuration for the Varonia application.
    /// Maps directly to the GlobalConfig.json file.
    /// </summary>
    [System.Serializable]
    public class GlobalConfig
    {
        [Header("Network")]
        /// <summary> The IP address of the main game server. </summary>
        public string ServerIP = "localhost"; 
        
        /// <summary> The IP address of the MQTT broker. </summary>
        public string MQTT_ServerIP = "localhost"; 
        
        /// <summary> Unique client identifier for the MQTT connection. </summary>
        public int MQTT_IDClient = 0; 

        [Header("Preferences")]
        /// <summary> Role of the device (e.g., Server_Player, Client_Spectator). </summary>
        public DeviceMode DeviceMode = DeviceMode.Server_Player; 
        
        /// <summary> Selected UI and localized content language. </summary>
        public string Language = "Fr"; 
        
        /// <summary> Player's dominant hand for input/VR. </summary>
        public MainHand MainHand = MainHand.Right;  
        
        /// <summary> Local display name for the player. </summary>
        public string PlayerName = "Varonia Player";


        public int hideMode;


        public bool Direct;
        
        
        
        
        
        [Header("Controller")]
        public Controller Controller = 0;

        /// <summary>
        /// Deserializes a JSON string into a GlobalConfig object using Newtonsoft.Json.
        /// </summary>
        /// <param name="jsonString">The raw JSON data.</param>
        /// <returns>A populated GlobalConfig object or null if deserialization fails.</returns>
        public static GlobalConfig CreateFromJson(string jsonString)
        {
            try 
            {
                return JsonConvert.DeserializeObject<GlobalConfig>(jsonString);
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[GlobalConfig] Deserialization Error: {e.Message}");
                return null; 
            }
        }
        
        
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }
}