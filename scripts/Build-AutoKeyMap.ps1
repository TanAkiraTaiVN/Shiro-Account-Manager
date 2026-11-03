# Build-AutoKeyMap.ps1: Generates scripts/i18n/auto-key-map.json for all 561 Auto_* keys
param()

$ErrorActionPreference = "Stop"

$map = [ordered]@{
    # === Group 1: Auto_AboutDialogaxaml (1) ===
    "Auto_AboutDialogaxaml_034" = "About.AccountsCount"

    # === Group 2: Auto_AccentThemeServi (4) ===
    "Auto_AccentThemeServi_335" = "AccentTheme.White"
    "Auto_AccentThemeServi_336" = "AccentTheme.Purple"
    "Auto_AccentThemeServi_337" = "AccentTheme.Green"
    "Auto_AccentThemeServi_338" = "AccentTheme.Pink"

    # === Group 3: Auto_AccountApi (1) ===
    "Auto_AccountApi_193" = "Account.RateLimited"

    # === Group 4: Auto_AccountBrowser (1) ===
    "Auto_AccountBrowser_196" = "AccountBrowser.ProxyError"

    # === Group 5: Auto_AccountBrowserDi (1) ===
    "Auto_AccountBrowserDi_195" = "AccountBrowser.BrowserNotFound"

    # === Group 6: Auto_AccountFieldsDia (1) ===
    "Auto_AccountFieldsDia_035" = "AccountFields.TitleFormat"

    # === Group 7: Auto_AccountLaunchSer (4) ===
    "Auto_AccountLaunchSer_230" = "AccountLaunch.InvalidAccount"
    "Auto_AccountLaunchSer_231" = "AccountLaunch.RateLimited"
    "Auto_AccountLaunchSer_232" = "AccountLaunch.TicketFailed"
    "Auto_AccountLaunchSer_233" = "AccountLaunch.PinAuthFailed"

    # === Group 8: Auto_AccountManagerAc (6) ===
    "Auto_AccountManagerAc_001" = "AccountManager.BulkLoginStopped"
    "Auto_AccountManagerAc_002" = "AccountManager.OpeningBrowser"
    "Auto_AccountManagerAc_003" = "AccountManager.NoValidCredentials"
    "Auto_AccountManagerAc_004" = "AccountManager.BulkLoginSummary"
    "Auto_AccountManagerAc_005" = "AccountManager.BulkLoginCompleted"
    "Auto_AccountManagerAc_006" = "AccountManager.CancelledByUser"

    # === Group 9: Auto_AccountManagerBa (2) ===
    "Auto_AccountManagerBa_007" = "AccountManager.VaultRestored"
    "Auto_AccountManagerBa_008" = "AccountManager.BackupRestored"

    # === Group 10: Auto_AccountManagerLi (2) ===
    "Auto_AccountManagerLi_009" = "AccountManager.ResumeBulkLoginPrompt"
    "Auto_AccountManagerLi_010" = "AccountManager.ResumeBulkLoginTitle"

    # === Group 11: Auto_AccountManagerMo (13) ===
    "Auto_AccountManagerMo_011" = "AccountManager.AddAccountBeforeLaunch"
    "Auto_AccountManagerMo_012" = "AccountManager.SelectAccountBeforeHome"
    "Auto_AccountManagerMo_013" = "AccountManager.OpeningHome"
    "Auto_AccountManagerMo_014" = "AccountManager.AddAccountBeforeJoin"
    "Auto_AccountManagerMo_015" = "AccountManager.SelectAccountBeforeJoin"
    "Auto_AccountManagerMo_016" = "AccountManager.EnterPlaceIdBeforeJoin"
    "Auto_AccountManagerMo_017" = "AccountManager.MultiRobloxFailedCloseClient"
    "Auto_AccountManagerMo_018" = "AccountManager.PreviousQueueStopping"
    "Auto_AccountManagerMo_019" = "AccountManager.JoiningServer"
    "Auto_AccountManagerMo_020" = "AccountManager.KilledRobloxProcesses"
    "Auto_AccountManagerMo_021" = "AccountManager.NoRobloxProcessesRunning"
    "Auto_AccountManagerMo_022" = "AccountManager.KilledRobloxProcessesExclamation"
    "Auto_AccountManagerMo_023" = "AccountManager.KilledAndActivatedMulti"

    # === Group 12: Auto_AccountManagerPe (5) ===
    "Auto_AccountManagerPe_024" = "AccountManager.EnterNewPasswordPrompt"
    "Auto_AccountManagerPe_025" = "AccountManager.ChangeDatabasePassword"
    "Auto_AccountManagerPe_026" = "AccountManager.SwitchedToDefaultVault"
    "Auto_AccountManagerPe_027" = "AccountManager.MasterPasswordMinLength"
    "Auto_AccountManagerPe_028" = "AccountManager.NewPasswordSet"

    # === Group 13: Auto_AccountManagerPr (4) ===
    "Auto_AccountManagerPr_029" = "AccountManager.MultiRobloxActive"
    "Auto_AccountManagerPr_030" = "AccountManager.AutoClosedRobloxActivatedMulti"
    "Auto_AccountManagerPr_031" = "AccountManager.RobloxRunningMultiInactive"
    "Auto_AccountManagerPr_032" = "AccountManager.MultiRobloxCannotActivate"

    # === Group 14: Auto_AccountSessionSt (1) ===
    "Auto_AccountSessionSt_194" = "Account.RateLimitCooldown"

    # === Group 15: Auto_AccountUtilsDial (14) ===
    "Auto_AccountUtilsDial_036" = "AccountUtils.TitleFormat"
    "Auto_AccountUtilsDial_037" = "AccountUtils.AccountNotFound"
    "Auto_AccountUtilsDial_038" = "AccountUtils.LogoutOtherSessionsPrompt"
    "Auto_AccountUtilsDial_039" = "AccountUtils.ConfirmTitle"
    "Auto_AccountUtilsDial_040" = "AccountUtils.SendingLogoutRequest"
    "Auto_AccountUtilsDial_041" = "AccountUtils.LogoutOtherSessionsFailed"
    "Auto_AccountUtilsDial_042" = "AccountUtils.LogoutOtherSessionsSuccess"
    "Auto_AccountUtilsDial_043" = "AccountUtils.PinMustBeFourDigits"
    "Auto_AccountUtilsDial_044" = "AccountUtils.UnlockingPin"
    "Auto_AccountUtilsDial_045" = "AccountUtils.UnlockPinFailed"
    "Auto_AccountUtilsDial_046" = "AccountUtils.UnlockPinSuccess"
    "Auto_AccountUtilsDial_047" = "AccountUtils.UnlockPinError"
    "Auto_AccountUtilsDial_048" = "AccountUtils.PasswordCleared"
    "Auto_AccountUtilsDial_049" = "AccountUtils.PasswordSaved"

    # === Group 16: Auto_AccountViewModel (12) ===
    "Auto_AccountViewModel_347" = "Account.StatusIssue"
    "Auto_AccountViewModel_348" = "Account.SessionExpiredTip"
    "Auto_AccountViewModel_349" = "Account.SessionUnknown"
    "Auto_AccountViewModel_350" = "Account.ShortSessionTip"
    "Auto_AccountViewModel_351" = "Account.HealthySessionTip"
    "Auto_AccountViewModel_352" = "Account.ValidSessionTip"
    "Auto_AccountViewModel_353" = "Account.OpeningRoblox"
    "Auto_AccountViewModel_354" = "Account.ShortSessionBadge"
    "Auto_AccountViewModel_355" = "Account.GroupLabel"
    "Auto_AccountViewModel_356" = "Account.ShowAction"
    "Auto_AccountViewModel_357" = "Account.RobuxNotUpdated"
    "Auto_AccountViewModel_358" = "Account.RobuxBalance"

    # === Group 17: Auto_AccountWorkflowS (61) ===
    "Auto_AccountWorkflowS_274" = "AccountWorkflow.CheckingAllSessions"
    "Auto_AccountWorkflowS_275" = "AccountWorkflow.StatusValid"
    "Auto_AccountWorkflowS_276" = "AccountWorkflow.CheckingProgressWithExpired"
    "Auto_AccountWorkflowS_277" = "AccountWorkflow.ScanCompletedWithBans"
    "Auto_AccountWorkflowS_278" = "AccountWorkflow.ScanCompletedAllHealthy"
    "Auto_AccountWorkflowS_279" = "AccountWorkflow.SessionCheckResultHeader"
    "Auto_AccountWorkflowS_280" = "AccountWorkflow.TotalAccountsSummary"
    "Auto_AccountWorkflowS_281" = "AccountWorkflow.ReadyAccountsSummary"
    "Auto_AccountWorkflowS_282" = "AccountWorkflow.ExpiredSessionsSummary"
    "Auto_AccountWorkflowS_283" = "AccountWorkflow.BannedAccountsSummary"
    "Auto_AccountWorkflowS_284" = "AccountWorkflow.BannedAccountsListHeader"
    "Auto_AccountWorkflowS_285" = "AccountWorkflow.MoreAccountsTruncated"
    "Auto_AccountWorkflowS_286" = "AccountWorkflow.SpecialNoteAllExpired"
    "Auto_AccountWorkflowS_287" = "AccountWorkflow.IpChangedWarning"
    "Auto_AccountWorkflowS_288" = "AccountWorkflow.CookieRotationAutoRenewed"
    "Auto_AccountWorkflowS_289" = "AccountWorkflow.CategorizedTabsNotification"
    "Auto_AccountWorkflowS_290" = "AccountWorkflow.ZeroExpiredSessions"
    "Auto_AccountWorkflowS_291" = "AccountWorkflow.ZeroBannedAccounts"
    "Auto_AccountWorkflowS_292" = "AccountWorkflow.AllAccountsValidAndRotated"
    "Auto_AccountWorkflowS_293" = "AccountWorkflow.SessionCheckDialogTitle"
    "Auto_AccountWorkflowS_294" = "AccountWorkflow.EnterNewCookiePrompt"
    "Auto_AccountWorkflowS_295" = "AccountWorkflow.UpdateCookieDialogTitle"
    "Auto_AccountWorkflowS_296" = "AccountWorkflow.ValidatingNewSession"
    "Auto_AccountWorkflowS_297" = "AccountWorkflow.UpdateCookieSuccess"
    "Auto_AccountWorkflowS_298" = "AccountWorkflow.InvalidCookieRetainedOld"
    "Auto_AccountWorkflowS_299" = "AccountWorkflow.InvalidOrExpiredCookie"
    "Auto_AccountWorkflowS_300" = "AccountWorkflow.EnterAliasPrompt"
    "Auto_AccountWorkflowS_301" = "AccountWorkflow.ChangeAliasTitle"
    "Auto_AccountWorkflowS_302" = "AccountWorkflow.AliasChanged"
    "Auto_AccountWorkflowS_303" = "AccountWorkflow.EnterGroupPrompt"
    "Auto_AccountWorkflowS_304" = "AccountWorkflow.AssignGroupTitle"
    "Auto_AccountWorkflowS_305" = "AccountWorkflow.MovedToGroup"
    "Auto_AccountWorkflowS_306" = "AccountWorkflow.EnterNotesPrompt"
    "Auto_AccountWorkflowS_307" = "AccountWorkflow.ChangeNotesTitle"
    "Auto_AccountWorkflowS_308" = "AccountWorkflow.NotesUpdated"
    "Auto_AccountWorkflowS_309" = "AccountWorkflow.UnpinnedAccount"
    "Auto_AccountWorkflowS_310" = "AccountWorkflow.PinnedAccount"
    "Auto_AccountWorkflowS_311" = "AccountWorkflow.CopiedUsername"
    "Auto_AccountWorkflowS_312" = "AccountWorkflow.CopiedUserPass"
    "Auto_AccountWorkflowS_313" = "AccountWorkflow.CopiedUserId"
    "Auto_AccountWorkflowS_314" = "AccountWorkflow.CopiedProfileLink"
    "Auto_AccountWorkflowS_315" = "AccountWorkflow.CopiedSecurityToken"
    "Auto_AccountWorkflowS_316" = "AccountWorkflow.DeleteAccountPrompt"
    "Auto_AccountWorkflowS_317" = "AccountWorkflow.ConfirmDeleteTitle"
    "Auto_AccountWorkflowS_318" = "AccountWorkflow.AccountDeleted"
    "Auto_AccountWorkflowS_319" = "AccountWorkflow.SelectAccountBeforeGroup"
    "Auto_AccountWorkflowS_320" = "AccountWorkflow.BatchGroupPrompt"
    "Auto_AccountWorkflowS_321" = "AccountWorkflow.BatchGroupTitle"
    "Auto_AccountWorkflowS_322" = "AccountWorkflow.BatchMovedToGroup"
    "Auto_AccountWorkflowS_323" = "AccountWorkflow.SelectAccountBeforeSavePlace"
    "Auto_AccountWorkflowS_324" = "AccountWorkflow.EnterValidPlaceBeforeSave"
    "Auto_AccountWorkflowS_325" = "AccountWorkflow.PlaceIdSaved"
    "Auto_AccountWorkflowS_326" = "AccountWorkflow.SelectAccountBeforeCopyPass"
    "Auto_AccountWorkflowS_327" = "AccountWorkflow.BatchCopiedUserPass"
    "Auto_AccountWorkflowS_328" = "AccountWorkflow.SelectAccountBeforeDelete"
    "Auto_AccountWorkflowS_329" = "AccountWorkflow.BatchDeletePrompt"
    "Auto_AccountWorkflowS_330" = "AccountWorkflow.BatchDeleteTitle"
    "Auto_AccountWorkflowS_331" = "AccountWorkflow.BatchAccountsDeleted"
    "Auto_AccountWorkflowS_332" = "AccountWorkflow.CheckingProgressSimple"
    "Auto_AccountWorkflowS_333" = "AccountWorkflow.SessionCheckCompleted"
    "Auto_AccountWorkflowS_334" = "AccountWorkflow.SessionCheckError"

    # === Group 18: Auto_AltGenDialogaxam (15) ===
    "Auto_AltGenDialogaxam_050" = "AltGen.EnterApiKey"
    "Auto_AltGenDialogaxam_051" = "AltGen.ConnectingApi"
    "Auto_AltGenDialogaxam_052" = "AltGen.LoadingState"
    "Auto_AltGenDialogaxam_053" = "AltGen.NoneState"
    "Auto_AltGenDialogaxam_054" = "AltGen.CreateSuccessWithGroup"
    "Auto_AltGenDialogaxam_055" = "AltGen.SaveToShiroFailed"
    "Auto_AltGenDialogaxam_056" = "AltGen.CreateSuccessTitle"
    "Auto_AltGenDialogaxam_057" = "AltGen.AccountAdded"
    "Auto_AltGenDialogaxam_058" = "AltGen.UnknownError"
    "Auto_AltGenDialogaxam_059" = "AltGen.FailedFormat"
    "Auto_AltGenDialogaxam_060" = "AltGen.CreateFailedGeneric"
    "Auto_AltGenDialogaxam_061" = "AltGen.CreateFailedTitle"
    "Auto_AltGenDialogaxam_062" = "AltGen.SystemErrorTitle"
    "Auto_AltGenDialogaxam_063" = "AltGen.NoHistory"
    "Auto_AltGenDialogaxam_064" = "AltGen.CannotOpenCsv"

    # === Group 19: Auto_AvaloniaDialogSe (2) ===
    "Auto_AvaloniaDialogSe_339" = "Dialog.NexusErrorTitle"
    "Auto_AvaloniaDialogSe_340" = "Dialog.NexusOpenFailed"

    # === Group 20: Auto_BloxGenService (4) ===
    "Auto_BloxGenService_200" = "BloxGen.EnterApiKey"
    "Auto_BloxGenService_201" = "BloxGen.RequestFailed"
    "Auto_BloxGenService_202" = "BloxGen.MissingCookieResponse"
    "Auto_BloxGenService_203" = "BloxGen.RequestCancelled"

    # === Group 21: Auto_BulkImportDialog (9) ===
    "Auto_BulkImportDialog_065" = "BulkImport.ContentCleared"
    "Auto_BulkImportDialog_066" = "BulkImport.PasteAccountListPrompt"
    "Auto_BulkImportDialog_067" = "BulkImport.NoValidAccountsFound"
    "Auto_BulkImportDialog_068" = "BulkImport.ImportingCookie"
    "Auto_BulkImportDialog_069" = "BulkImport.SavingAccount"
    "Auto_BulkImportDialog_070" = "BulkImport.Completed"
    "Auto_BulkImportDialog_071" = "BulkImport.OpeningInAppLogin"
    "Auto_BulkImportDialog_072" = "BulkImport.StoppedWithSuccessCount"
    "Auto_BulkImportDialog_073" = "BulkImport.SummaryResult"

    # === Group 22: Auto_CommandPaletteCo (1) ===
    "Auto_CommandPaletteCo_341" = "CommandPalette.CharDWithStroke"

    # === Group 23: Auto_DiscordRpcServic (5) ===
    "Auto_DiscordRpcServic_251" = "DiscordRpc.PlayingGame"
    "Auto_DiscordRpcServic_252" = "DiscordRpc.ManagingAccounts"
    "Auto_DiscordRpcServic_253" = "DiscordRpc.ClientsOnline"
    "Auto_DiscordRpcServic_254" = "DiscordRpc.ReadyToLaunch"
    "Auto_DiscordRpcServic_255" = "DiscordRpc.StreamerMode"

    # === Group 24: Auto_FastLoginCoordin (26) ===
    "Auto_FastLoginCoordin_204" = "FastLogin.WrongPasswordKeyword"
    "Auto_FastLoginCoordin_205" = "FastLogin.InvalidCredentialsKeyword"
    "Auto_FastLoginCoordin_206" = "FastLogin.FourteenDaysKeyword"
    "Auto_FastLoginCoordin_207" = "FastLogin.SevenDaysKeyword"
    "Auto_FastLoginCoordin_208" = "FastLogin.ThreeDaysKeyword"
    "Auto_FastLoginCoordin_209" = "FastLogin.OneDayKeyword"
    "Auto_FastLoginCoordin_210" = "FastLogin.PermanentKeyword"
    "Auto_FastLoginCoordin_211" = "FastLogin.DeletedKeyword"
    "Auto_FastLoginCoordin_212" = "FastLogin.WarningKeyword"
    "Auto_FastLoginCoordin_213" = "FastLogin.LockedKeyword"
    "Auto_FastLoginCoordin_214" = "FastLogin.CookieExpiredOpeningBan"
    "Auto_FastLoginCoordin_215" = "FastLogin.BannedSevenDays"
    "Auto_FastLoginCoordin_216" = "FastLogin.BanSevenDaysShort"
    "Auto_FastLoginCoordin_217" = "FastLogin.BannedThreeDays"
    "Auto_FastLoginCoordin_218" = "FastLogin.BanThreeDaysShort"
    "Auto_FastLoginCoordin_219" = "FastLogin.BannedOneDay"
    "Auto_FastLoginCoordin_220" = "FastLogin.BanOneDayShort"
    "Auto_FastLoginCoordin_221" = "FastLogin.BannedFourteenDays"
    "Auto_FastLoginCoordin_222" = "FastLogin.BanFourteenDaysShort"
    "Auto_FastLoginCoordin_223" = "FastLogin.AccountDeletedPermanent"
    "Auto_FastLoginCoordin_224" = "FastLogin.BanPermanentShort"
    "Auto_FastLoginCoordin_225" = "FastLogin.ModerationWarning"
    "Auto_FastLoginCoordin_226" = "FastLogin.SecurityLockout"
    "Auto_FastLoginCoordin_227" = "FastLogin.SecurityLockoutShort"
    "Auto_FastLoginCoordin_228" = "FastLogin.AccountBannedOrSuspended"
    "Auto_FastLoginCoordin_229" = "FastLogin.IncorrectUsernameOrPassword"

    # === Group 25: Auto_FastLoginDialoga (19) ===
    "Auto_FastLoginDialoga_082" = "FastLogin.DialogProgressTitle"
    "Auto_FastLoginDialoga_083" = "FastLogin.DialogSingleTitle"
    "Auto_FastLoginDialoga_084" = "FastLogin.AutoFillUserPassTip"
    "Auto_FastLoginDialoga_085" = "FastLogin.AutoFillUserOnlyTip"
    "Auto_FastLoginDialoga_086" = "FastLogin.WebViewInitFailed"
    "Auto_FastLoginDialoga_087" = "FastLogin.LoadingSecureLoginPage"
    "Auto_FastLoginDialoga_088" = "FastLogin.OpeningModerationPage"
    "Auto_FastLoginDialoga_089" = "FastLogin.CannotLoadLoginPage"
    "Auto_FastLoginDialoga_090" = "FastLogin.AutoFillingAndLoggingIn"
    "Auto_FastLoginDialoga_091" = "FastLogin.AutoFillingUsername"
    "Auto_FastLoginDialoga_092" = "FastLogin.LoginPageReady"
    "Auto_FastLoginDialoga_093" = "FastLogin.PasteCookiePrompt"
    "Auto_FastLoginDialoga_094" = "FastLogin.CheckingRobloxApi"
    "Auto_FastLoginDialoga_095" = "FastLogin.CookieValidAndVerified"
    "Auto_FastLoginDialoga_096" = "FastLogin.InvalidAccountTitle"
    "Auto_FastLoginDialoga_097" = "FastLogin.CookieExpiredOrInvalid"
    "Auto_FastLoginDialoga_098" = "FastLogin.EnterValidCookiePrompt"
    "Auto_FastLoginDialoga_099" = "FastLogin.AccountAddedSuccess"
    "Auto_FastLoginDialoga_100" = "FastLogin.CookieFailedOrExpired"

    # === Group 26: Auto_FastLoginDialogS (8) ===
    "Auto_FastLoginDialogS_074" = "FastLogin.SessionExpiredCodeOpening"
    "Auto_FastLoginDialogS_075" = "FastLogin.CookieExpiredTransitioning"
    "Auto_FastLoginDialogS_076" = "FastLogin.TransitioningToNext"
    "Auto_FastLoginDialogS_077" = "FastLogin.AccountLockedOrModerated"
    "Auto_FastLoginDialogS_078" = "FastLogin.LockedOrBannedBadge"
    "Auto_FastLoginDialogS_079" = "FastLogin.SessionCapturedRotating"
    "Auto_FastLoginDialogS_080" = "FastLogin.InAppLoginSuccess"
    "Auto_FastLoginDialogS_081" = "FastLogin.SessionLockedOrModerated"

    # === Group 27: Auto_GameSearchItemVi (1) ===
    "Auto_GameSearchItemVi_359" = "ServerList.PlayerCount"

    # === Group 28: Auto_InputDialogaxaml (1) ===
    "Auto_InputDialogaxaml_101" = "InputDialog.PasswordKeyword"

    # === Group 29: Auto_LaunchCoordinato (15) ===
    "Auto_LaunchCoordinato_234" = "Launch.CaptchaRequiredTitle"
    "Auto_LaunchCoordinato_235" = "Launch.CaptchaPromptMessage"
    "Auto_LaunchCoordinato_236" = "Launch.CaptchaProgressWarning"
    "Auto_LaunchCoordinato_237" = "Launch.CaptchaIncompleteWarning"
    "Auto_LaunchCoordinato_238" = "Launch.CaptchaTabClosedWarning"
    "Auto_LaunchCoordinato_239" = "Launch.RateLimitedIpWaiting"
    "Auto_LaunchCoordinato_240" = "Launch.RateLimitedRetryWaiting"
    "Auto_LaunchCoordinato_241" = "Launch.AccountBannedLog"
    "Auto_LaunchCoordinato_242" = "Launch.AccountBannedStatus"
    "Auto_LaunchCoordinato_243" = "Launch.CheckBanButtonTip"
    "Auto_LaunchCoordinato_244" = "Launch.SessionExpiredLog"
    "Auto_LaunchCoordinato_245" = "Launch.SessionExpiredCardTip"
    "Auto_LaunchCoordinato_246" = "Launch.CheckBanButtonAlert"
    "Auto_LaunchCoordinato_247" = "Launch.SessionExpiredAlert"
    "Auto_LaunchCoordinato_248" = "Launch.QueueProgressWaiting"

    # === Group 30: Auto_MainViewModel (2) ===
    "Auto_MainViewModel_520" = "Main.ShuffleJobIdEnabled"
    "Auto_MainViewModel_521" = "Main.ShuffleJobIdDisabled"

    # === Group 31: Auto_MainViewModelAcc (10) ===
    "Auto_MainViewModelAcc_360" = "Main.EnterCookieTitle"
    "Auto_MainViewModelAcc_361" = "Main.PasteCookiePrompt"
    "Auto_MainViewModelAcc_362" = "Main.AccountAddedViaCookie"
    "Auto_MainViewModelAcc_363" = "Main.CookieInvalidOrExpired"
    "Auto_MainViewModelAcc_364" = "Main.ClientSessionCleared"
    "Auto_MainViewModelAcc_365" = "Main.CannotClearClientSession"
    "Auto_MainViewModelAcc_366" = "Main.SessionExpiredToast"
    "Auto_MainViewModelAcc_367" = "Main.SessionValidToast"
    "Auto_MainViewModelAcc_368" = "Main.SelectAccountBeforeRelogin"
    "Auto_MainViewModelAcc_369" = "Main.SelectAccountBeforeCheckSession"

    # === Group 32: Auto_MainViewModelCom (101) ===
    "Auto_MainViewModelCom_370" = "Main.AccountBadgeFormat"
    "Auto_MainViewModelCom_371" = "Main.SelectEnterKey"
    "Auto_MainViewModelCom_372" = "Main.AccountSelected"
    "Auto_MainViewModelCom_373" = "Main.ExecuteEnterKey"
    "Auto_MainViewModelCom_374" = "Main.AntiAfkDescription"
    "Auto_MainViewModelCom_375" = "Main.ToggleAntiAfkTitle"
    "Auto_MainViewModelCom_376" = "Main.ZeroGpuDescription"
    "Auto_MainViewModelCom_377" = "Main.ToggleZeroGpuTitle"
    "Auto_MainViewModelCom_378" = "Main.PotatoModeDescription"
    "Auto_MainViewModelCom_379" = "Main.TogglePotatoModeTitle"
    "Auto_MainViewModelCom_380" = "Main.AudioCategory"
    "Auto_MainViewModelCom_381" = "Main.SmartAutoMuteDescription"
    "Auto_MainViewModelCom_382" = "Main.ToggleSmartAutoMuteTitle"
    "Auto_MainViewModelCom_383" = "Main.RobloxAudioUnmuted"
    "Auto_MainViewModelCom_384" = "Main.RobloxAudioMuted"
    "Auto_MainViewModelCom_385" = "Main.ToggleMuteAllDescription"
    "Auto_MainViewModelCom_386" = "Main.ToggleMuteAllTitle"
    "Auto_MainViewModelCom_387" = "Main.SecurityCategory"
    "Auto_MainViewModelCom_388" = "Main.StreamerModeDescription"
    "Auto_MainViewModelCom_389" = "Main.ToggleStreamerModeTitle"
    "Auto_MainViewModelCom_390" = "Main.PerformanceCategory"
    "Auto_MainViewModelCom_391" = "Main.OptimizeRamDescription"
    "Auto_MainViewModelCom_392" = "Main.OptimizeRamTitle"
    "Auto_MainViewModelCom_393" = "Main.SystemCategory"
    "Auto_MainViewModelCom_394" = "Main.CleanTrayDescription"
    "Auto_MainViewModelCom_395" = "Main.CleanTrayTitle"
    "Auto_MainViewModelCom_396" = "Main.WindowsCategory"
    "Auto_MainViewModelCom_397" = "Main.ArrangeAutoDescription"
    "Auto_MainViewModelCom_398" = "Main.ArrangeAutoTitle"
    "Auto_MainViewModelCom_399" = "Main.ArrangeGrid2x2Description"
    "Auto_MainViewModelCom_400" = "Main.ArrangeGrid2x2Title"
    "Auto_MainViewModelCom_401" = "Main.ArrangeGrid3x2Description"
    "Auto_MainViewModelCom_402" = "Main.ArrangeGrid3x2Title"
    "Auto_MainViewModelCom_403" = "Main.ArrangeCascadeDescription"
    "Auto_MainViewModelCom_404" = "Main.ArrangeCascadeTitle"
    "Auto_MainViewModelCom_405" = "Main.KillAllRobloxDescription"
    "Auto_MainViewModelCom_406" = "Main.KillAllRobloxTitle"
    "Auto_MainViewModelCom_407" = "Main.CheckAllSessionsDescription"
    "Auto_MainViewModelCom_408" = "Main.CheckAllSessionsTitle"
    "Auto_MainViewModelCom_409" = "Main.ReloginExpiredDescription"
    "Auto_MainViewModelCom_410" = "Main.ReloginExpiredTitle"
    "Auto_MainViewModelCom_411" = "Main.RefreshRobuxDescription"
    "Auto_MainViewModelCom_412" = "Main.RefreshRobuxTitle"
    "Auto_MainViewModelCom_413" = "Main.SelectAllAccountsDescription"
    "Auto_MainViewModelCom_414" = "Main.DeselectAllAccountsDescription"
    "Auto_MainViewModelCom_415" = "Main.DeselectAllAccountsTitle"
    "Auto_MainViewModelCom_416" = "Main.InvertSelectionDescription"
    "Auto_MainViewModelCom_417" = "Main.InvertSelectionTitle"
    "Auto_MainViewModelCom_418" = "Main.QuickSelect5Description"
    "Auto_MainViewModelCom_419" = "Main.QuickSelect5Title"
    "Auto_MainViewModelCom_420" = "Main.QuickSelect10Description"
    "Auto_MainViewModelCom_421" = "Main.QuickSelect10Title"
    "Auto_MainViewModelCom_422" = "Main.QuickSelect20Description"
    "Auto_MainViewModelCom_423" = "Main.QuickSelect20Title"
    "Auto_MainViewModelCom_424" = "Main.SelectRangeDescription"
    "Auto_MainViewModelCom_425" = "Main.SelectRangeTitle"
    "Auto_MainViewModelCom_426" = "Main.NavigationCategory"
    "Auto_MainViewModelCom_427" = "Main.OpenServerListDescription"
    "Auto_MainViewModelCom_428" = "Main.OpenServerListTitle"
    "Auto_MainViewModelCom_429" = "Main.OpenRecentGamesDescription"
    "Auto_MainViewModelCom_430" = "Main.OpenRecentGamesTitle"
    "Auto_MainViewModelCom_431" = "Main.OpenSettingsDescription"
    "Auto_MainViewModelCom_432" = "Main.OpenSettingsTitle"
    "Auto_MainViewModelCom_433" = "Main.OpenAccountsDescription"
    "Auto_MainViewModelCom_434" = "Main.OpenAccountsTitle"
    "Auto_MainViewModelCom_435" = "Main.ToolsCategory"
    "Auto_MainViewModelCom_436" = "Main.OpenProxyManagerDescription"
    "Auto_MainViewModelCom_437" = "Main.OpenProxyManagerTitle"
    "Auto_MainViewModelCom_438" = "Main.OpenNexusDescription"
    "Auto_MainViewModelCom_439" = "Main.OpenNexusTitle"
    "Auto_MainViewModelCom_440" = "Main.OpenAltGenDescription"
    "Auto_MainViewModelCom_441" = "Main.OpenAltGenTitle"
    "Auto_MainViewModelCom_442" = "Main.SuperOptimizationSuccess"
    "Auto_MainViewModelCom_443" = "Main.MemoryOptimizedStatus"
    "Auto_MainViewModelCom_444" = "Main.RestoredRobloxWindows"
    "Auto_MainViewModelCom_445" = "Main.ZeroGpuActivated"
    "Auto_MainViewModelCom_446" = "Main.ZeroGpuNoWindowsOpen"
    "Auto_MainViewModelCom_447" = "Main.AntiAfkActivated"
    "Auto_MainViewModelCom_448" = "Main.AntiAfkDeactivated"
    "Auto_MainViewModelCom_449" = "Main.PotatoModeActivated"
    "Auto_MainViewModelCom_450" = "Main.PotatoModeDeactivated"
    "Auto_MainViewModelCom_451" = "Main.WindowsArrangedSuccess"
    "Auto_MainViewModelCom_452" = "Main.NoValidAccountsForRobux"
    "Auto_MainViewModelCom_453" = "Main.RefreshingRobuxMultiple"
    "Auto_MainViewModelCom_454" = "Main.RefreshingRobuxProgress"
    "Auto_MainViewModelCom_455" = "Main.RefreshingRobuxCompleted"
    "Auto_MainViewModelCom_456" = "Main.CheckingRobuxSingle"
    "Auto_MainViewModelCom_457" = "Main.RobuxBalanceResult"
    "Auto_MainViewModelCom_458" = "Main.CannotFetchRobux"
    "Auto_MainViewModelCom_459" = "Main.PresenceStatusUpdated"
    "Auto_MainViewModelCom_460" = "Main.SessionCheckErrorPrefix"
    "Auto_MainViewModelCom_461" = "Main.NoNormalExpiredAccounts"
    "Auto_MainViewModelCom_462" = "Main.NoExpiredAccountsFound"
    "Auto_MainViewModelCom_463" = "Main.ReloggingInExpiredAccounts"
    "Auto_MainViewModelCom_464" = "Main.BatchReloginErrorPrefix"
    "Auto_MainViewModelCom_465" = "Main.KilledRobloxCount"
    "Auto_MainViewModelCom_466" = "Main.TrayCleanedSuccess"
    "Auto_MainViewModelCom_467" = "Main.TrayCleanedGhostIcons"
    "Auto_MainViewModelCom_468" = "Main.SystemTrayTitle"
    "Auto_MainViewModelCom_469" = "Main.GameSelected"
    "Auto_MainViewModelCom_470" = "Main.GameSelectedWithJoinPrompt"

    # === Group 33: Auto_MainViewModelFea (16) ===
    "Auto_MainViewModelFea_471" = "Main.StreamerModeActivated"
    "Auto_MainViewModelFea_472" = "Main.ZeroGpuActivatedCount"
    "Auto_MainViewModelFea_473" = "Main.OpenRobloxHomeAction"
    "Auto_MainViewModelFea_474" = "Main.JoinServerAction"
    "Auto_MainViewModelFea_475" = "Main.AccountsSelectedCount"
    "Auto_MainViewModelFea_476" = "Main.TotalAccountsCount"
    "Auto_MainViewModelFea_477" = "Main.ShiroReadyStatus"
    "Auto_MainViewModelFea_478" = "Main.ReadyAccountsCount"
    "Auto_MainViewModelFea_479" = "Main.SelectedCountShort"
    "Auto_MainViewModelFea_480" = "Main.TabAllCount"
    "Auto_MainViewModelFea_481" = "Main.TabPlayingCount"
    "Auto_MainViewModelFea_482" = "Main.TabReadyCount"
    "Auto_MainViewModelFea_483" = "Main.TabExpiredCount"
    "Auto_MainViewModelFea_484" = "Main.TabBannedCount"
    "Auto_MainViewModelFea_485" = "Main.TabPinnedCount"
    "Auto_MainViewModelFea_486" = "Main.DeselectCountAction"

    # === Group 34: Auto_MainViewModelLau (11) ===
    "Auto_MainViewModelLau_487" = "Main.AccountModeratedOrLocked"
    "Auto_MainViewModelLau_488" = "Main.OpeningModerationBrowser"
    "Auto_MainViewModelLau_489" = "Main.ViewBanTitle"
    "Auto_MainViewModelLau_490" = "Main.OpeningBanDetailsPrompt"
    "Auto_MainViewModelLau_491" = "Main.ClosedRobloxForAccount"
    "Auto_MainViewModelLau_492" = "Main.NoRobloxRunningForAccount"
    "Auto_MainViewModelLau_493" = "Main.EnterPlaceIdOrLink"
    "Auto_MainViewModelLau_494" = "Main.NoAccountSelectedTitle"
    "Auto_MainViewModelLau_495" = "Main.SelectAccountBeforeOpenHome"
    "Auto_MainViewModelLau_496" = "Main.SelectAccountBeforePlayServer"
    "Auto_MainViewModelLau_497" = "Main.MissingPlaceIdTitle"

    # === Group 35: Auto_MainViewModelUiC (15) ===
    "Auto_MainViewModelUiC_498" = "Main.QuickSelectedReady"
    "Auto_MainViewModelUiC_499" = "Main.NoReadyAccountsInList"
    "Auto_MainViewModelUiC_500" = "Main.InvertedSelectionResult"
    "Auto_MainViewModelUiC_501" = "Main.EnterSelectionRangePrompt"
    "Auto_MainViewModelUiC_502" = "Main.SelectByRangeTitle"
    "Auto_MainViewModelUiC_503" = "Main.SelectedByRangeResult"
    "Auto_MainViewModelUiC_504" = "Main.NoAccountsMatchedRange"
    "Auto_MainViewModelUiC_505" = "Main.LaunchQueueStopped"
    "Auto_MainViewModelUiC_506" = "Main.LaunchQueueStoppedTitle"
    "Auto_MainViewModelUiC_507" = "Main.BroughtWindowToFront"
    "Auto_MainViewModelUiC_508" = "Main.WindowNotFoundForAccount"
    "Auto_MainViewModelUiC_509" = "Main.WindowsArrangedWithLayout"
    "Auto_MainViewModelUiC_510" = "Main.ArrangeWindowsTitle"
    "Auto_MainViewModelUiC_511" = "Main.SmartAutoMuteActivated"
    "Auto_MainViewModelUiC_512" = "Main.SmartAutoMuteDeactivated"

    # === Group 36: Auto_MainViewModelUiU (7) ===
    "Auto_MainViewModelUiU_513" = "Main.SortNameDesc"
    "Auto_MainViewModelUiU_514" = "Main.SortOriginal"
    "Auto_MainViewModelUiU_515" = "Main.SortPlayingFirst"
    "Auto_MainViewModelUiU_516" = "Main.SortRobuxDesc"
    "Auto_MainViewModelUiU_517" = "Main.SortNameAsc"
    "Auto_MainViewModelUiU_518" = "Main.LaunchQueueProgress"
    "Auto_MainViewModelUiU_519" = "Main.PreparingClientLaunch"

    # === Group 37: Auto_MainWindowaxaml (11) ===
    "Auto_MainWindowaxaml_112" = "MainWindow.VaultKeyChangedPrompt"
    "Auto_MainWindowaxaml_113" = "MainWindow.RecoverShiroDataTitle"
    "Auto_MainWindowaxaml_114" = "MainWindow.RecoverySuccessTitle"
    "Auto_MainWindowaxaml_115" = "MainWindow.EnterMasterPasswordPrompt"
    "Auto_MainWindowaxaml_116" = "MainWindow.UnlockShiroTitle"
    "Auto_MainWindowaxaml_117" = "MainWindow.UnlockedTitle"
    "Auto_MainWindowaxaml_118" = "MainWindow.AccountsLoadedCount"
    "Auto_MainWindowaxaml_119" = "MainWindow.IncorrectMasterPassword"
    "Auto_MainWindowaxaml_120" = "MainWindow.WrongPasswordTitle"
    "Auto_MainWindowaxaml_121" = "MainWindow.CannotDecryptAccountDataPrompt"
    "Auto_MainWindowaxaml_122" = "MainWindow.RecoverDataTitle"

    # === Group 38: Auto_MainWindowNaviga (10) ===
    "Auto_MainWindowNaviga_102" = "MainWindow.ConfirmCloseRobloxSelectedPrompt"
    "Auto_MainWindowNaviga_103" = "MainWindow.ConfirmCloseRobloxBatchTitle"
    "Auto_MainWindowNaviga_104" = "MainWindow.ClosedRobloxForSelected"
    "Auto_MainWindowNaviga_105" = "MainWindow.NoRobloxRunningForSelected"
    "Auto_MainWindowNaviga_106" = "MainWindow.ConfirmKillAllRobloxPrompt"
    "Auto_MainWindowNaviga_107" = "MainWindow.ConfirmKillAllRobloxTitle"
    "Auto_MainWindowNaviga_108" = "MainWindow.MutedAllRobloxClients"
    "Auto_MainWindowNaviga_109" = "MainWindow.UnmutedAllRobloxClients"
    "Auto_MainWindowNaviga_110" = "MainWindow.StreamerModeEnabled"
    "Auto_MainWindowNaviga_111" = "MainWindow.StreamerModeDisabled"

    # === Group 39: Auto_MissingAssetsDia (26) ===
    "Auto_MissingAssetsDia_123" = "MissingAssets.LoadingText"
    "Auto_MissingAssetsDia_124" = "MissingAssets.PriceChecking"
    "Auto_MissingAssetsDia_125" = "MissingAssets.TitleFormat"
    "Auto_MissingAssetsDia_126" = "MissingAssets.UnownedAssetsDetected"
    "Auto_MissingAssetsDia_127" = "MissingAssets.AccountNotFound"
    "Auto_MissingAssetsDia_128" = "MissingAssets.LoadingAssetInfoCount"
    "Auto_MissingAssetsDia_129" = "MissingAssets.LoadingProgress"
    "Auto_MissingAssetsDia_130" = "MissingAssets.LoadingCompleted"
    "Auto_MissingAssetsDia_131" = "MissingAssets.TotalRobux"
    "Auto_MissingAssetsDia_132" = "MissingAssets.ItemNumber"
    "Auto_MissingAssetsDia_133" = "MissingAssets.PriceFree"
    "Auto_MissingAssetsDia_134" = "MissingAssets.PriceRobux"
    "Auto_MissingAssetsDia_135" = "MissingAssets.PriceOffSale"
    "Auto_MissingAssetsDia_136" = "MissingAssets.PriceOutOfStock"
    "Auto_MissingAssetsDia_137" = "MissingAssets.PriceUnavailable"
    "Auto_MissingAssetsDia_138" = "MissingAssets.PriceLoadError"
    "Auto_MissingAssetsDia_139" = "MissingAssets.NoValidAccountInfo"
    "Auto_MissingAssetsDia_140" = "MissingAssets.NoValidProductId"
    "Auto_MissingAssetsDia_141" = "MissingAssets.ConfirmPurchasePrompt"
    "Auto_MissingAssetsDia_142" = "MissingAssets.ConfirmPurchaseTitle"
    "Auto_MissingAssetsDia_143" = "MissingAssets.PurchasingText"
    "Auto_MissingAssetsDia_144" = "MissingAssets.CannotGetCsrfToken"
    "Auto_MissingAssetsDia_145" = "MissingAssets.PurchasedBadge"
    "Auto_MissingAssetsDia_146" = "MissingAssets.PurchaseSuccess"
    "Auto_MissingAssetsDia_147" = "MissingAssets.PurchaseFailed"
    "Auto_MissingAssetsDia_148" = "MissingAssets.TransactionError"

    # === Group 40: Auto_NexusWindowaxaml (15) ===
    "Auto_NexusWindowaxaml_149" = "Nexus.InvalidPort"
    "Auto_NexusWindowaxaml_150" = "Nexus.StartingServer"
    "Auto_NexusWindowaxaml_151" = "Nexus.ServerRunning"
    "Auto_NexusWindowaxaml_152" = "Nexus.StopServerAction"
    "Auto_NexusWindowaxaml_153" = "Nexus.ServerStartedOnPort"
    "Auto_NexusWindowaxaml_154" = "Nexus.ServerStoppedStatus"
    "Auto_NexusWindowaxaml_155" = "Nexus.StartServerAction"
    "Auto_NexusWindowaxaml_156" = "Nexus.ServerStartError"
    "Auto_NexusWindowaxaml_157" = "Nexus.StoppingServer"
    "Auto_NexusWindowaxaml_158" = "Nexus.ServerStoppedNotification"
    "Auto_NexusWindowaxaml_159" = "Nexus.ServerStopError"
    "Auto_NexusWindowaxaml_160" = "Nexus.ConnectedClientsCount"
    "Auto_NexusWindowaxaml_161" = "Nexus.EnterScriptPrompt"
    "Auto_NexusWindowaxaml_162" = "Nexus.ScriptSentSelected"
    "Auto_NexusWindowaxaml_163" = "Nexus.ScriptSentAll"

    # === Group 41: Auto_PrerequisiteServ (1) ===
    "Auto_PrerequisiteServ_249" = "Prerequisite.VcRedistInstallError"

    # === Group 42: Auto_Presence (1) ===
    "Auto_Presence_197" = "Presence.SessionExpiredBadge"

    # === Group 43: Auto_ProxyManagerDial (10) ===
    "Auto_ProxyManagerDial_164" = "ProxyManager.DuplicatesAndErrorsSummary"
    "Auto_ProxyManagerDial_165" = "ProxyManager.AddedProxiesCount"
    "Auto_ProxyManagerDial_166" = "ProxyManager.ProxyAlreadyExists"
    "Auto_ProxyManagerDial_167" = "ProxyManager.InvalidProxyFormat"
    "Auto_ProxyManagerDial_168" = "ProxyManager.TotalProxiesCount"
    "Auto_ProxyManagerDial_169" = "ProxyManager.NoProxiesToCheck"
    "Auto_ProxyManagerDial_170" = "ProxyManager.CheckingProxiesProgress"
    "Auto_ProxyManagerDial_171" = "ProxyManager.CheckSummaryResult"
    "Auto_ProxyManagerDial_172" = "ProxyManager.DeletedFailedProxies"
    "Auto_ProxyManagerDial_173" = "ProxyManager.NoFailedProxiesToDelete"

    # === Group 44: Auto_ProxyManagerServ (4) ===
    "Auto_ProxyManagerServ_256" = "ProxyManager.NoAuthentication"
    "Auto_ProxyManagerServ_257" = "ProxyManager.TestingStatus"
    "Auto_ProxyManagerServ_258" = "ProxyManager.NotTestedStatus"
    "Auto_ProxyManagerServ_259" = "ProxyManager.TimeoutOrErrorStatus"

    # === Group 45: Auto_RobloxBrowserSig (2) ===
    "Auto_RobloxBrowserSig_198" = "RobloxSignup.OpenedBrowser"
    "Auto_RobloxBrowserSig_199" = "RobloxSignup.SuccessAddedAccount"

    # === Group 46: Auto_RobloxLauncherSe (1) ===
    "Auto_RobloxLauncherSe_250" = "RobloxLauncher.PlayerExeNotFound"

    # === Group 47: Auto_RobloxModeration (10) ===
    "Auto_RobloxModeration_260" = "Moderation.CannotGetSessionInfo"
    "Auto_RobloxModeration_261" = "Moderation.ReactivatedSuccess"
    "Auto_RobloxModeration_262" = "Moderation.ReactivateFailed"
    "Auto_RobloxModeration_263" = "Moderation.ConnectionError"
    "Auto_RobloxModeration_264" = "Moderation.CannotGetSessionForNotice"
    "Auto_RobloxModeration_265" = "Moderation.WarningReceived"
    "Auto_RobloxModeration_266" = "Moderation.AccountBannedWithDuration"
    "Auto_RobloxModeration_267" = "Moderation.AccountBannedGeneric"
    "Auto_RobloxModeration_268" = "Moderation.NoSpecificViolationFound"
    "Auto_RobloxModeration_269" = "Moderation.LoadNoticeError"

    # === Group 48: Auto_RobloxServerQuer (2) ===
    "Auto_RobloxServerQuer_270" = "ServerQuery.FetchingRegion"
    "Auto_RobloxServerQuer_271" = "ServerQuery.UnknownRegion"

    # === Group 49: Auto_RobloxWindowArra (4) ===
    "Auto_RobloxWindowArra_342" = "WindowArranger.WindowsOnly"
    "Auto_RobloxWindowArra_343" = "WindowArranger.NoClientsRunning"
    "Auto_RobloxWindowArra_344" = "WindowArranger.NoWindowsFound"
    "Auto_RobloxWindowArra_345" = "WindowArranger.MinimizeToCorner"

    # === Group 50: Auto_RobloxWindowServ (1) ===
    "Auto_RobloxWindowServ_346" = "WindowService.GridDimensionsFormat"

    # === Group 51: Auto_ServerItemViewMo (1) ===
    "Auto_ServerItemViewMo_522" = "ServerList.NotLoaded"

    # === Group 52: Auto_ServerListViewMo (39) ===
    "Auto_ServerListViewMo_523" = "ServerList.GameSelected"
    "Auto_ServerListViewMo_524" = "ServerList.LaunchingGame"
    "Auto_ServerListViewMo_525" = "ServerList.SelectAccountBeforePlay"
    "Auto_ServerListViewMo_526" = "ServerList.GameAlreadyFavorite"
    "Auto_ServerListViewMo_527" = "ServerList.AddedToFavorites"
    "Auto_ServerListViewMo_528" = "ServerList.RemovedFromFavorites"
    "Auto_ServerListViewMo_529" = "ServerList.FavoriteGameSelected"
    "Auto_ServerListViewMo_530" = "ServerList.LaunchingFavoriteGame"
    "Auto_ServerListViewMo_531" = "ServerList.EnterPlayerUsername"
    "Auto_ServerListViewMo_532" = "ServerList.EnterPlaceIdFirst"
    "Auto_ServerListViewMo_533" = "ServerList.SearchingPlayer"
    "Auto_ServerListViewMo_534" = "ServerList.PlayerUserIdNotFound"
    "Auto_ServerListViewMo_535" = "ServerList.CannotGetPlayerAvatar"
    "Auto_ServerListViewMo_536" = "ServerList.PlayerAvatarNotExist"
    "Auto_ServerListViewMo_537" = "ServerList.ScanningServersForPlayer"
    "Auto_ServerListViewMo_538" = "ServerList.PlayerFoundInServer"
    "Auto_ServerListViewMo_539" = "ServerList.PlayerNotFound"
    "Auto_ServerListViewMo_540" = "ServerList.PlayerSearchCancelled"
    "Auto_ServerListViewMo_541" = "ServerList.PlayerSearchError"
    "Auto_ServerListViewMo_542" = "ServerList.ServerSelected"
    "Auto_ServerListViewMo_543" = "ServerList.JobIdSelectedAndCopied"
    "Auto_ServerListViewMo_544" = "ServerList.JobIdCopied"
    "Auto_ServerListViewMo_545" = "ServerList.InvalidPlaceId"
    "Auto_ServerListViewMo_546" = "ServerList.SelectAccountBeforeJoinServer"
    "Auto_ServerListViewMo_547" = "ServerList.RandomServerKeyword"
    "Auto_ServerListViewMo_548" = "ServerList.JoiningServerWithAccounts"
    "Auto_ServerListViewMo_549" = "ServerList.RegionsLoadedComplete"
    "Auto_ServerListViewMo_550" = "ServerList.LoadingRegionsAndPinging"
    "Auto_ServerListViewMo_551" = "ServerList.LoadingRegionsCancelled"
    "Auto_ServerListViewMo_552" = "ServerList.RecentGameSelected"
    "Auto_ServerListViewMo_553" = "ServerList.LaunchingRecentGame"
    "Auto_ServerListViewMo_554" = "ServerList.OperationCancelled"
    "Auto_ServerListViewMo_555" = "ServerList.EnterValidPlaceBeforeLoad"
    "Auto_ServerListViewMo_556" = "ServerList.FindingServersForPlace"
    "Auto_ServerListViewMo_557" = "ServerList.LoadServersFailed"
    "Auto_ServerListViewMo_558" = "ServerList.LoadedServersCount"
    "Auto_ServerListViewMo_559" = "ServerList.LoadedServersSuccess"
    "Auto_ServerListViewMo_560" = "ServerList.NoServersFoundForPlace"
    "Auto_ServerListViewMo_561" = "ServerList.LoadingServersCancelled"

    # === Group 53: Auto_SettingsViewaxam (8) ===
    "Auto_SettingsViewaxam_174" = "Settings.SaveSuccess"
    "Auto_SettingsViewaxam_175" = "Settings.SaveError"
    "Auto_SettingsViewaxam_176" = "Settings.CleanActionTitle"
    "Auto_SettingsViewaxam_177" = "Settings.CleanTrayRequestSent"
    "Auto_SettingsViewaxam_178" = "Settings.AvatarCacheClearedFiles"
    "Auto_SettingsViewaxam_179" = "Settings.AvatarCacheRefreshedEmpty"
    "Auto_SettingsViewaxam_180" = "Settings.ClearAvatarCacheError"
    "Auto_SettingsViewaxam_181" = "Settings.ChangeMasterPasswordError"

    # === Group 54: Auto_UpdateCheckServi (2) ===
    "Auto_UpdateCheckServi_272" = "UpdateCheck.NewUpdateTitle"
    "Auto_UpdateCheckServi_273" = "UpdateCheck.NewVersionAvailable"

    # === Group 55: Auto_UpdaterWindowaxa (11) ===
    "Auto_UpdaterWindowaxa_182" = "Updater.UpdateFailed"
    "Auto_UpdaterWindowaxa_183" = "Updater.ErrorOccurredTitle"
    "Auto_UpdaterWindowaxa_184" = "Updater.AlreadyLatestVersion"
    "Auto_UpdaterWindowaxa_185" = "Updater.NoNewUpdates"
    "Auto_UpdaterWindowaxa_186" = "Updater.ExtractingPackage"
    "Auto_UpdaterWindowaxa_187" = "Updater.CompletedRestarting"
    "Auto_UpdaterWindowaxa_188" = "Updater.CheckingGitHub"
    "Auto_UpdaterWindowaxa_189" = "Updater.CurrentIsLatest"
    "Auto_UpdaterWindowaxa_190" = "Updater.DownloadingVersion"
    "Auto_UpdaterWindowaxa_191" = "Updater.DownloadingSize"
    "Auto_UpdaterWindowaxa_192" = "Updater.CannotConnectToServer"

    # === Group 56: Auto_ValueConverters (1) ===
    "Auto_ValueConverters_033" = "Converters.DefaultGroup"
}

Write-Host "Total mapped keys defined: $($map.Count)"

# Validate against regex ^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$
$pattern = '^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$'
$invalid = @()
foreach ($k in $map.Keys) {
    $target = $map[$k]
    if ($target -notmatch $pattern) {
        $invalid += "$k -> $target"
    }
}

if ($invalid.Count -gt 0) {
    Write-Error ("Invalid target keys found:`n" + ($invalid -join "`n"))
    exit 1
}

# Verify against vi.json to make sure every single Auto_ key is mapped
$viRaw = Get-Content 'src/Shiro/Resources/Strings/vi.json' -Encoding UTF8 -Raw | ConvertFrom-Json
$viAutoKeys = $viRaw.PSObject.Properties.Name | Where-Object { $_ -like 'Auto_*' }

$missingFromMap = $viAutoKeys | Where-Object { -not $map.Contains($_) }
if ($missingFromMap.Count -gt 0) {
    Write-Error ("Keys missing from map ($($missingFromMap.Count)):`n" + ($missingFromMap -join "`n"))
    exit 1
}

$extraInMap = $map.Keys | Where-Object { $viAutoKeys -notcontains $_ }
if ($extraInMap.Count -gt 0) {
    Write-Error ("Extra keys in map ($($extraInMap.Count)):`n" + ($extraInMap -join "`n"))
    exit 1
}

# Serialize to scripts/i18n/auto-key-map.json with 2 spaces indentation
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("{")
$allKeys = @($map.Keys)
for ($i = 0; $i -lt $allKeys.Count; $i++) {
    $k = $allKeys[$i]
    $v = $map[$k]
    $comma = if ($i -lt $allKeys.Count - 1) { "," } else { "" }
    [void]$sb.AppendLine("  `"$k`": `"$v`"$comma")
}
[void]$sb.Append("}")

$outPath = "scripts/i18n/auto-key-map.json"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($outPath, $sb.ToString(), $utf8NoBom)

Write-Host "Successfully generated $outPath with $($map.Count) keys mapped!"
