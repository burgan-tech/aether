using System.Collections.Generic;

namespace BBT.Aether.HttpClient;

/*
{
   "ApiEndPoints": {
       "FakeApi": {
           "BaseUrl": "",
           "DefaultTimeOut": 20,
           "DefaultMediaTypeWithQualityHeaderValue": "application/json",
           "DefaultRequestHeaders": {
               "FakeHeader": ""
           },
           "Authentication": {
               "Type": "OAuth",
               "Data": {
                   "OAuth": {
                       "TokenUrl": "",
                       "ClientId": "",
                       "ClientSecret": "",
                       "Scopes": ""
                   }
               }
           }
       }
   }
}
 */

public class ApiEndPointOptions
{
    public required string BaseUrl { get; set; }
    public int? DefaultTimeOut { get; set; } = 20;
    public string DefaultMediaTypeWithQualityHeaderValue { get; set; } = "application/json";
    public Dictionary<string, string> DefaultRequestHeaders { get; set; } = new();
    public ApiEndPointAuthenticationOptions? Authentication { get; set; }
}

public class ApiEndPointAuthenticationOptions
{
    public string? Type { get; set; }
    public Dictionary<string, string> Data { get; set; } = new();
}