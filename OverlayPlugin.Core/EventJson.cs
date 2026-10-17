using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin
{
    // Fork: an event's JSON, made once however many overlays and WebSocket clients receive it
    // (each made it again: hundreds of log lines a second times every receiver).  Kept on the
    // event itself, which receivers don't change.
    public static class EventJson
    {
        private sealed class Cached
        {
            public string Json;
        }

        public static string Of(JObject e)
        {
            var cached = e.Annotation<Cached>();
            if (cached != null)
                return cached.Json;
            var json = e.ToString(Formatting.None);
            e.AddAnnotation(new Cached { Json = json });
            return json;
        }
    }
}
