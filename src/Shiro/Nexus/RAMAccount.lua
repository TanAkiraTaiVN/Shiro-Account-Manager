-- Shiro Account Manager • Lua Automation Controller

local Account = {} Account.__index = Account

local WebserverSettings = {
    Port = '7963',
    Password = ''
}

function WebserverSettings:SetPort(Port) self.Port = Port end
function WebserverSettings:SetPassword(Password) self.Password = Password end

local HttpService = game:GetService'HttpService'
local Request = request or http_request or (http and http.request) or (fluxus and fluxus.request) or (getgenv and getgenv().request)

local function BuildHeaders()
    local headers = {}
    if WebserverSettings.Password and #WebserverSettings.Password >= 6 then
        headers['X-RAM-Auth'] = WebserverSettings.Password
        headers['Authorization'] = 'Bearer ' .. WebserverSettings.Password
    end
    return headers
end

local function GET(Method, Account, ...)
    local Arguments = {...}
    local Url = 'http://localhost:' .. WebserverSettings.Port .. '/' .. Method .. '?Account=' .. (Account or '')

    for Index, Parameter in pairs(Arguments) do
        if typeof(Parameter) == 'boolean' then continue end
        
        Url = Url .. '&' .. Parameter
    end

    local Response = Request {
        Method = 'GET',
        Url = Url,
        Headers = BuildHeaders()
    }

    if Response.StatusCode ~= 200 then return false end

    return Response.Body
end

local function POST(Method, Account, Body, ...)
    local Arguments = {...}
    local Url = 'http://localhost:' .. WebserverSettings.Port .. '/' .. Method .. '?Account=' .. (Account or '')

    for Index, Parameter in pairs(Arguments) do
        Url = Url .. '&' .. Parameter
    end
    
    local Response = Request {
        Method = 'POST',
        Url = Url,
        Headers = BuildHeaders(),
        Body = Body
    }

    if Response.StatusCode ~= 200 then return false end

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

function Account:BlockUser(Argument)
    if typeof(Argument) == 'string' then
        return GET('BlockUser', self.Username, 'UserId=' .. Argument)
    elseif typeof(Argument) == 'Instance' and Argument:IsA'Player' then
        return self:BlockUser(tostring(Argument.UserId))
    elseif typeof(Argument) == 'number' then
        return self:BlockUser(tostring(Argument))
    end
end
function Account:UnblockUser(Argument)
    if typeof(Argument) == 'string' then
        return GET('UnblockUser', self.Username, 'UserId=' .. Argument)
    elseif typeof(Argument) == 'Instance' and Argument:IsA'Player' then
        return self:UnblockUser(tostring(Argument.UserId))
    elseif typeof(Argument) == 'number' then
        return self:UnblockUser(tostring(Argument))
    end
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

function Account:SetServer(PlaceId, JobId) return GET('SetServer', self.Username, 'PlaceId=' .. PlaceId, 'JobId=' .. JobId) end
function Account:SetRecommendedServer(PlaceId) return GET('SetServer', self.Username, 'PlaceId=' .. PlaceId) end

function Account:ImportCookie(Token) return GET('ImportCookie', '', 'Cookie=' .. HttpService:UrlEncode(Token)) end
function Account:GetCookie() return GET('GetCookie', self.Username) end
function Account:LaunchAccount(PlaceId, JobId, FollowUser, JoinVip) -- if you want to follow someone, PlaceId must be their user id
    return GET('LaunchAccount', self.Username, 'PlaceId=' .. PlaceId, JobId and ('JobId=' .. JobId), FollowUser and 'FollowUser=true', JoinVip and 'JoinVIP=true')
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

    local wsUrl = 'ws://localhost:' .. WebserverSettings.Port .. '/ws'
    if WebserverSettings.Password and #WebserverSettings.Password >= 6 then
        wsUrl = wsUrl .. '?token=' .. HttpService:UrlEncode(WebserverSettings.Password)
    end

    local ws = WebSocketConnect(wsUrl)
    if not ws then return false, 'Failed to connect to Shiro WebSocket Gateway' end

    ws.OnMessage:Connect(function(Message)
        local success, parsed = pcall(function() return HttpService:JSONDecode(Message) end)
        if success and parsed then
            if parsed.Event == 'anti_afk_pulse' then
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