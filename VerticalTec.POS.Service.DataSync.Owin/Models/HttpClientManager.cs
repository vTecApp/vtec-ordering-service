﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;

namespace VerticalTec.POS.Service.DataSync.Owin.Models
{
    public class HttpClientManager
    {
        public static HttpClientManager _instance;
        static object sync = new object();

        public static HttpClientManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (sync)
                    {
                        if (_instance == null)
                            _instance = new HttpClientManager();
                    }
                }
                return _instance;
            }
        }

        HttpClient _httpClient;

        HttpClientManager()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        }

        public async Task<TResult> PostAsync<TResult>(string url, object payload)
        {
            var data = payload is string ? payload.ToString() : JsonConvert.SerializeObject(payload);
            var content = new StringContent(data, Encoding.UTF8, "application/json");
            var respMessage = await _httpClient.PostAsync(url, content);
            var respContent = await respMessage.Content.ReadAsStringAsync();
            TResult respBody = default;
            try
            {
                respBody = await Task.Run(() => JsonConvert.DeserializeObject<TResult>(respContent));
            }
            catch (Exception) { }
            if (respMessage.IsSuccessStatusCode)
            {
                return respBody;
            }
            else
            {
                throw new HttpResponseException(respMessage);
            }
        }

        public async Task<TResult> VDSPostAsync<TResult>(string url, object payload)
        {
            var data = payload is string ? payload.ToString() : JsonConvert.SerializeObject(payload);
            var content = new StringContent(data, Encoding.UTF8, "application/json");
            var respMessage = await _httpClient.PostAsync(url, content);
            var respContent = await respMessage.Content.ReadAsStringAsync();
            ResponseBody<TResult> respBody = null;

            respBody = await Task.Run(() =>
            {
                try
                {
                    return JsonConvert.DeserializeObject<ResponseBody<TResult>>(respContent);
                }
                catch (Exception)
                {
                    return null;
                }
            });

            if (respMessage.IsSuccessStatusCode)
            {
                return respBody.Data;
            }
            else
            {
                if (respBody != null)
                    respMessage.ReasonPhrase = respBody.Message;
                throw new HttpResponseException(respMessage);
            }
        }

        public TimeSpan ConnTimeOut
        {
            set
            {
                try
                {
                    _httpClient.Timeout = value;
                }
                catch (Exception) { }
            }
        }
    }
}
