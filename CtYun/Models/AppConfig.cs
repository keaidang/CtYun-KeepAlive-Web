using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CtYun.Models
{
    public class AppConfig
    {
        [JsonPropertyName("accounts")]
        public List<AccountConfig> Accounts { get; set; } = [];

        [JsonPropertyName("keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 60;

        [JsonPropertyName("adminPassword")]
        public string AdminPassword { get; set; } = "admin";
    }

    public class LoginRequest
    {
        [JsonPropertyName("password")]
        public string Password { get; set; }
    }

    public class ChangePasswordRequest
    {
        [JsonPropertyName("oldPassword")]
        public string OldPassword { get; set; }

        [JsonPropertyName("newPassword")]
        public string NewPassword { get; set; }
    }

    public class AccountConfig
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }

        [JsonPropertyName("deviceCode")]
        public string DeviceCode { get; set; }
    }

    public class AccountStatusDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; }

        [JsonPropertyName("isRunning")]
        public bool IsRunning { get; set; }

        [JsonPropertyName("statusText")]
        public string StatusText { get; set; }

        [JsonPropertyName("desktops")]
        public List<DesktopStatusDto> Desktops { get; set; } = [];
    }

    public class DesktopStatusDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }
    }

    public class VerifySmsRequest
    {
        [JsonPropertyName("user")]
        public string User { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; }
    }

    public class AccountActionRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class WebResponseBase
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("msg")]
        public string Msg { get; set; }
    }
}
