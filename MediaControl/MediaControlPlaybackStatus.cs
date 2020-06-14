using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage.Streams;

namespace MediaControl
{
    class MediaControlPlaybackStatus
    {
        public String title { get; set; }
        public String artist { get; set; }
        public String album { get; set; }
        public bool playing { get; set; }
        public IRandomAccessStreamReference thumbnail { get; set; }
        private static void addString(JsonObject jsonObject, String key, String value)
        {
            jsonObject[key] = String.IsNullOrEmpty(value) ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(value);
        }
        public string Stringify()
        {
            JsonObject jsonObject = new JsonObject();
            addString(jsonObject, "title", title);
            addString(jsonObject, "artist", artist);
            addString(jsonObject, "album", album);
            jsonObject["playing"] = JsonValue.CreateBooleanValue(playing);
            return jsonObject.Stringify();
        }
    }
}
