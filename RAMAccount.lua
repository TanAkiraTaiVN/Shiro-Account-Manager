-- Shiro Account Manager • Lua Automation Controller

local Account = {} Account.__index = Account

local WebserverSettings = {
    Host = 'localhost',
    Port = '7963',
    Password = ''
}

function WebserverSettings:SetHost(Host)
    if type(Host) == 'string' and #Host > 0 then
        self.Host = Host
    end
end
function WebserverSettings:SetPort(Port) self.Port = tostring(Port) end
function WebserverSettings:SetPassword(Password) self.Password = Password or '' end

local HttpService = game:GetService'HttpService'
local Request = request or http_request or (http and http.request) or (fluxus and fluxus.request) or (getgenv and getgenv().request)

local function IsLoopbackHost(host)
    if type(host) ~= 'string' then return false end
    local h = string.lower(host)
    return h == 'localhost' or h == '127.0.0.1' or h == '::1' or h == '[::1]'
end

-- Localhost connections require >= 6 chars; external (non-loopback) connections require >= 12 chars.
local function GetMinPasswordLength()
    return IsLoopbackHost(WebserverSettings.Host) and 6 or 12
end

local function BuildHeaders()
    local headers = {}
    if WebserverSettings.Password and #WebserverSettings.Password >= GetMinPasswordLength() then
        headers['X-Shiro-Auth'] = WebserverSettings.Password
        headers['X-RAM-Auth'] = WebserverSettings.Password
        headers['Authorization'] = 'Bearer ' .. WebserverSettings.Password
    end
    return headers
end

local function BuildUrl(Method, AccountName, ...)
    local Arguments = {...}
    local Url = 'http://' .. WebserverSettings.Host .. ':' .. WebserverSettings.Port .. '/' .. Method .. '?Account=' .. HttpService:UrlEncode(tostring(AccountName or ''))

    for _, Parameter in pairs(Arguments) do
        if typeof(Parameter) ~= 'boolean' and Parameter ~= nil then
            Url = Url .. '&' .. tostring(Parameter)
        end
    end
    return Url
end

local function GET(Method, AccountName, ...)
    if not Request then return false end
    local ok, Response = pcall(Request, {
        Method = 'GET',
        Url = BuildUrl(Method, AccountName, ...),
        Headers = BuildHeaders()
    })

    if not ok or not Response or Response.StatusCode ~= 200 then return false end

    return Response.Body
end

local function POST(Method, AccountName, Body, ...)
    if not Request then return false end
    local headers = BuildHeaders()
    headers['Content-Type'] = 'text/plain; charset=utf-8'
    local ok, Response = pcall(Request, {
        Method = 'POST',
        Url = BuildUrl(Method, AccountName, ...),
        Headers = headers,
        Body = Body or ''
    })

    if not ok or not Response or Response.StatusCode ~= 200 then return false end

    return Response.Body
end

function Account.new(Username, SkipValidation)
    local self = {} setmetatable(self, Account)

    local IsValid = SkipValidation or GET('GetCSRFToken', Username)

    if not IsValid or IsValid == 'Invalid Account' then return false end

    self.Username = Username

    return self
end

function Account:GetCSRFToken() return GET('GetCSRFToken', self.Username) end

local function ParsePositiveUserId(Argument)
    if typeof(Argument) == 'Instance' and Argument:IsA'Player' then
        Argument = Argument.UserId
    end
    local num = tonumber(Argument)
    if not num or num <= 0 or math.floor(num) ~= num then
        return nil
    end
    return tostring(math.floor(num))
end

function Account:BlockUser(Argument)
    local uid = ParsePositiveUserId(Argument)
    if not uid then return false end
    return GET('BlockUser', self.Username, 'UserId=' .. uid)
end
function Account:UnblockUser(Argument)
    local uid = ParsePositiveUserId(Argument)
    if not uid then return false end
    return GET('UnblockUser', self.Username, 'UserId=' .. uid)
end
function Account:GetBlockedList() return GET('GetBlockedList', self.Username) end
function Account:UnblockEveryone() return GET('UnblockEveryone', self.Username) end

function Account:GetAlias() return GET('GetAlias', self.Username) end
function Account:GetDescription() return GET('GetDescription', self.Username) end
function Account:SetAlias(Alias) return POST('SetAlias', self.Username, Alias) end
function Account:SetDescription(Description) return POST('SetDescription', self.Username, Description) end
function Account:AppendDescription(Description) return POST('AppendDescription', self.Username, Description) end

function Account:GetField(Field) return GET('GetField', self.Username, 'Field=' .. HttpService:UrlEncode(Field)) end
function Account:SetField(Field, Value) return GET('SetField', self.Username, 'Field=' .. HttpService:UrlEncode(Field), 'Value=' .. HttpService:UrlEncode(tostring(Value))) end
function Account:RemoveField(Field) return GET('RemoveField', self.Username, 'Field=' .. HttpService:UrlEncode(Field)) end

function Account:SetServer(PlaceId, JobId) return GET('SetServer', self.Username, 'PlaceId=' .. HttpService:UrlEncode(tostring(PlaceId)), 'JobId=' .. HttpService:UrlEncode(tostring(JobId))) end
function Account:SetRecommendedServer(PlaceId) return GET('SetRecommendedServer', self.Username, PlaceId and ('PlaceId=' .. HttpService:UrlEncode(tostring(PlaceId)))) end

function Account:ImportCookie(Token) return POST('ImportCookie', self.Username, tostring(Token or '')) end
function Account:GetCookie() return GET('GetCookie', self.Username) end
function Account:LaunchAccount(PlaceId, JobId, FollowUser, JoinVip) -- if you want to follow someone, PlaceId must be their user id
    return GET('LaunchAccount', self.Username, 'PlaceId=' .. HttpService:UrlEncode(tostring(PlaceId)), JobId and ('JobId=' .. HttpService:UrlEncode(tostring(JobId))), FollowUser and 'FollowUser=true', JoinVip and 'JoinVIP=true')
end

-- ===================================================================
-- Modern Shiro Performance & Window Control Endpoints
-- ===================================================================
function Account.TrimMemory() return GET('TrimMemory') end
function Account.ToggleWindows() return GET('ToggleWindows') end
function Account.HideWindows() return GET('HideWindows') end
function Account.ShowWindows() return GET('ShowWindows') end
function Account.TogglePotatoMode() return GET('PotatoMode/Toggle') end
function Account.ToggleAntiAfk() return GET('ToggleAntiAfk') end
function Account.GetAntiAfkStatus() return GET('AntiAfk/Status') end
function Account.ToggleMultiRoblox() return GET('MultiRoblox/Toggle') end
function Account.GetMultiRobloxStatus() return GET('MultiRoblox/Status') end
function Account.GetWebSocketStats() return GET('GetWebSocketStats') end

-- ===================================================================
-- Native Luau Anti-AFK (Zero Win32 Overhead via VirtualUser)
-- ===================================================================
local antiAfkConnection = nil

function Account.EnableInGameAntiAfk()
    if antiAfkConnection then return true end
    local Players = game:GetService('Players')
    local VirtualUser = game:GetService('VirtualUser')
    local LocalPlayer = Players.LocalPlayer or Players.PlayerAdded:Wait()

    antiAfkConnection = LocalPlayer.Idled:Connect(function()
        VirtualUser:CaptureController()
        VirtualUser:ClickButton2(Vector2.zero)
    end)
    return true
end

function Account.DisableInGameAntiAfk()
    if antiAfkConnection then
        antiAfkConnection:Disconnect()
        antiAfkConnection = nil
        return true
    end
    return false
end

-- ===================================================================
-- Real-time WebSocket Event Client (Supported in modern executors)
-- ===================================================================
function Account.ConnectEvents(Callback)
    local WebSocketConnect = (syn and syn.websocket and syn.websocket.connect)
        or (WebSocket and WebSocket.connect)
        or (websocket and websocket.connect)

    if not WebSocketConnect then
        return false, 'Executor does not support WebSocket client'
    end

    if not IsLoopbackHost(WebserverSettings.Host) and (not WebserverSettings.Password or #WebserverSettings.Password < 12) then
        return false, 'External WebSocket connections require a password of at least 12 characters'
    end

    local wsUrl = 'ws://' .. WebserverSettings.Host .. ':' .. WebserverSettings.Port .. '/ws'
    if WebserverSettings.Password and #WebserverSettings.Password >= GetMinPasswordLength() then
        wsUrl = wsUrl .. '?token=' .. HttpService:UrlEncode(WebserverSettings.Password)
    end

    local ws = WebSocketConnect(wsUrl)
    if not ws then return false, 'Failed to connect to Shiro WebSocket Gateway' end

    ws.OnMessage:Connect(function(Message)
        local success, parsed = pcall(function() return HttpService:JSONDecode(Message) end)
        if success and parsed then
            if (parsed.event or parsed.Event) == 'anti_afk_pulse' then
                pcall(function()
                    local VirtualUser = game:GetService('VirtualUser')
                    VirtualUser:CaptureController()
                    VirtualUser:ClickButton2(Vector2.zero)
                end)
            end
            if Callback then
                Callback(parsed)
            end
        end
    end)

    return ws
end

return Account, WebserverSettings