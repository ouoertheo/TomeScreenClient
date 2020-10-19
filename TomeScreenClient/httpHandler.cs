using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Configuration;

namespace TomeScreenClient
{


    public static class httpHandler
    {
        private static string timeServerURL;

        static public void setTimeServerUrl(string url)
        {
            timeServerURL = url;
        }

        static public async Task<HttpResponseMessage> postToTimeServerAsync(Activity activity)
        {
            FileLogger.Log("Posting to: " + timeServerURL,3);
            using (HttpClient client = new HttpClient())
            {
                client.BaseAddress = new Uri(timeServerURL);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response;
                try
                {
                    response = await client.PostAsJsonAsync("/poll", activity);
                    return response;
                } catch {
                    throw new HttpRequestException("Failed to connect");
                }
            }
        }

        static public async Task<HttpResponseMessage> getActivityFromTimeServer(String _user)
        {
            using (HttpClient client = new HttpClient())
            {
                client.BaseAddress = new Uri(timeServerURL);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                FileLogger.Log("Getting activities from: " + timeServerURL+ "/getToday/" + Uri.EscapeUriString(_user),3);
                try
                {
                    HttpResponseMessage response = await client.GetAsync("/getToday/" + Uri.EscapeUriString(_user));
                    return response;
                } catch (Exception e)
                {
                    throw new HttpRequestException("Failed to connect");
                }
            }
        }

    }
    public class timeServerData
    {
        public string _id { get; set; }
        public string user { get; set; }
        public string device { get; set; }
        public DateTime timestamp { get; set;}
        public string activity { get; set;}
        public long usage { get; set; }
    }
    // TODO: Add in detection via resource usage.
}
