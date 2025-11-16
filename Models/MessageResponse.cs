using System.Text.Json.Serialization;

namespace apiAKS_NetCore.Models
{
    public class MessageResponse
    {
        public MessageResponse(string mensaje) => Mensaje = mensaje;

        [JsonPropertyName("mensaje")]
        public string Mensaje { get; set; }
    }
}
