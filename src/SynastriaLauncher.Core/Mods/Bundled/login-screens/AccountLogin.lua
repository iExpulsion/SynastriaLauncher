FADE_IN_TIME = 2;
DEFAULT_TOOLTIP_COLOR = {0.8, 0.8, 0.8, 0.09, 0.09, 0.09};
MAX_PIN_LENGTH = 10;

function AccountLogin_OnLoad(self)
	TOSFrame.noticeType = "EULA";

	self:RegisterEvent("SHOW_SERVER_ALERT");
	self:RegisterEvent("SHOW_SURVEY_NOTIFICATION");
	self:RegisterEvent("CLIENT_ACCOUNT_MISMATCH");
	self:RegisterEvent("CLIENT_TRIAL");
	self:RegisterEvent("SCANDLL_ERROR");
	self:RegisterEvent("SCANDLL_FINISHED");

	local versionType, buildType, version, internalVersion, date = GetBuildInfo();
	AccountLoginVersion:SetFormattedText(VERSION_TEMPLATE, versionType, version, internalVersion, buildType, date);

	-- Color edit box backdrops
	local backdropColor = DEFAULT_TOOLTIP_COLOR;
	AccountLoginAccountEdit:SetBackdropBorderColor(backdropColor[1], backdropColor[2], backdropColor[3]);
	AccountLoginAccountEdit:SetBackdropColor(backdropColor[4], backdropColor[5], backdropColor[6]);
	AccountLoginPasswordEdit:SetBackdropBorderColor(backdropColor[1], backdropColor[2], backdropColor[3]);
	AccountLoginPasswordEdit:SetBackdropColor(backdropColor[4], backdropColor[5], backdropColor[6]);
	AccountLoginTokenEdit:SetBackdropBorderColor(backdropColor[1], backdropColor[2], backdropColor[3]);
	AccountLoginTokenEdit:SetBackdropColor(backdropColor[4], backdropColor[5], backdropColor[6]);
	TokenEnterDialogBackgroundEdit:SetBackdropBorderColor(backdropColor[1], backdropColor[2], backdropColor[3]);
	TokenEnterDialogBackgroundEdit:SetBackdropColor(backdropColor[4], backdropColor[5], backdropColor[6]);

	self:SetCamera(0);
	self:SetSequence(0);
	
	if (IsStreamingTrial()) then
		AccountLoginCinematicsButton:Disable();
		AccountLogin:SetModel("Interface\\Glues\\Models\\UI_MainMenu\\UI_MainMenu.m2");
	else
		AccountLogin:SetModel("Interface\\Glues\\Models\\UI_MainMenu_Northrend\\UI_MainMenu_Northrend.m2");
	end

	AccountLoginManageAccountButton:Hide();
	AccountLoginCommunityButton:Hide();
	AccountLoginCreditsButton:Hide();
	AccountLoginTOSButton:Hide();

	-- The ESRB notice's frame name varies by build and server.
	local esrbFrames = {
		"AccountLoginESRBFrame",
		"AccountLoginESRBNotice",
		"AccountLoginESRB",
		"ESRBNotice",
		"ESRBFrame",
	};
	for _, name in ipairs(esrbFrames) do
		if _G[name] then _G[name]:Hide() end
	end

	AccountLoginCinematicsButton:ClearAllPoints();
	AccountLoginCinematicsButton:SetPoint("BOTTOM", AccountLoginExitButton, "TOP", 0, 5);
	OptionsButton:ClearAllPoints();
	OptionsButton:SetPoint("BOTTOM", AccountLoginCinematicsButton, "TOP", 0, -2);
	OptionsButton:Show();
end

function AccountLogin_OnShow(self)
	self:SetSequence(0);
	PlayGlueMusic(CurrentGlueMusic);
	PlayGlueAmbience(GlueAmbienceTracks["DARKPORTAL"], 4.0);

	-- Try to show the EULA or the TOS
	AccountLogin_ShowUserAgreements();
	
	local serverName = GetServerName();
	if(serverName) then
		AccountLoginRealmName:SetText(serverName);
	else
		AccountLoginRealmName:Hide()
	end

	local accountName = GetSavedAccountName();

	AccountLoginAccountEdit:SetText(accountName);
	AccountLoginPasswordEdit:SetText("");
	-- An earlier version kept the password here in plain text (Remember Password); auto login replaced it.
	if ( GetSavedAccountList() ~= "" ) then
		SetSavedAccountList("");
	end
	
	--[[AccountLoginTokenEdit:SetText("");
	if ( accountName and accountName ~= "" and GetUsesToken() ) then
		AccountLoginTokenEdit:Show()
	else
		AccountLoginTokenEdit:Hide()
	end
	
	AccountLogin_SetupAccountListDDL();]]--
	
	if( IsTrialAccount() ) then
		AccountLoginUpgradeAccountButton:Show();
	else
		AccountLoginUpgradeAccountButton:Hide();
	end

	ACCOUNT_MSG_NUM_AVAILABLE = 0;
	ACCOUNT_MSG_PRIORITY = 0;
	ACCOUNT_MSG_HEADERS_LOADED = false;
	ACCOUNT_MSG_BODY_LOADED = false;
	ACCOUNT_MSG_CURRENT_INDEX = nil;
end

function AccountLogin_OnHide(self)
	-- Stop the sounds from the login screen (like the dragon roaring etc)
	StopAllSFX( 1.0 );
	if ( not AccountLoginSaveAccountName:GetChecked() ) then
		SetSavedAccountList("");
	end
end

function AccountLogin_FocusPassword()
	AccountLoginPasswordEdit:SetFocus();
end

function AccountLogin_FocusAccountName()
	AccountLoginAccountEdit:SetFocus();
end

function AccountLogin_OnKeyDown(key)
	if ( key == "ESCAPE" ) then
		if ( ConnectionHelpFrame:IsShown() ) then
			ConnectionHelpFrame:Hide();
			AccountLoginUI:Show();
		elseif ( SurveyNotificationFrame:IsShown() ) then
			-- do nothing
		else
			AccountLogin_Exit();
		end
	elseif ( key == "ENTER" ) then
		if ( not TOSAccepted() ) then
			return;
		elseif ( TOSFrame:IsShown() or ConnectionHelpFrame:IsShown() ) then
			return;
		elseif ( SurveyNotificationFrame:IsShown() ) then
			AccountLogin_SurveyNotificationDone(1);
		end
		AccountLogin_Login();
	elseif ( key == "PRINTSCREEN" ) then
		Screenshot();
	end
end

function AccountLogin_OnEvent(event, arg1, arg2, arg3)
	if ( event == "SHOW_SERVER_ALERT" ) then
		ServerAlertText:SetText(arg1);
		ServerAlertFrame:Show();
	elseif ( event == "SHOW_SURVEY_NOTIFICATION" ) then
		AccountLogin_ShowSurveyNotification();
	elseif ( event == "CLIENT_ACCOUNT_MISMATCH" ) then
		local accountExpansionLevel = arg1;
		local installationExpansionLevel = arg2;
		if ( accountExpansionLevel == 1 ) then
			GlueDialog_Show("CLIENT_ACCOUNT_MISMATCH", CLIENT_ACCOUNT_MISMATCH_BC);	
		else
			GlueDialog_Show("CLIENT_ACCOUNT_MISMATCH", CLIENT_ACCOUNT_MISMATCH_LK);	
		end
	elseif ( event == "CLIENT_TRIAL" ) then
		GlueDialog_Show("CLIENT_TRIAL");
	elseif ( event == "SCANDLL_ERROR" ) then
		GlueDialog:Hide();
		ScanDLLContinueAnyway();
		AccountLoginUI:Show();
	elseif ( event == "SCANDLL_FINISHED" ) then
		if ( arg1 == "OK" ) then
			GlueDialog:Hide();
			AccountLoginUI:Show();
		else
			AccountLogin.hackURL = _G["SCANDLL_URL_"..arg1];
			AccountLogin.hackName = arg2;
			AccountLogin.hackType = arg1;
			local formatString = _G["SCANDLL_MESSAGE_"..arg1];
			if ( arg3 == 1 ) then
				formatString = _G["SCANDLL_MESSAGE_HACKNOCONTINUE"];
			end
			local msg = format(formatString, AccountLogin.hackName, AccountLogin.hackURL);
			if ( arg3 == 1 ) then
				GlueDialog_Show("SCANDLL_HACKFOUND_NOCONTINUE", msg);
			else
				GlueDialog_Show("SCANDLL_HACKFOUND", msg);
			end
			PlaySoundFile("Sound\\Creature\\MobileAlertBot\\MobileAlertBotIntruderAlert01.wav");
		end
	end
end

function AccountLogin_Login()
	PlaySound("gsLogin");
	DefaultServerLogin(AccountLoginAccountEdit:GetText(), AccountLoginPasswordEdit:GetText());
	CharacterSelect._loggedInAccount = AccountLoginAccountEdit:GetText(); -- [Reorder] per-account order

	if ( AccountLoginSaveAccountName:GetChecked() ) then
		SetSavedAccountName(AccountLoginAccountEdit:GetText());
	else
		SetSavedAccountName("");
		-- SetUsesToken(false);
	end
end

function AccountLogin_TOS()
	if ( not GlueDialog:IsShown() ) then
		PlaySound("gsLoginNewAccount");
		AccountLoginUI:Hide();
		TOSFrame:Show();
		TOSScrollFrameScrollBar:SetValue(0);		
		TOSScrollFrame:Show();
		TOSFrameTitle:SetText(TOS_FRAME_TITLE);
		TOSText:Show();
	end
end

function AccountLogin_ManageAccount()
	PlaySound("gsLoginNewAccount");
	LaunchURL(AUTH_NO_TIME_URL);
end

function AccountLogin_LaunchCommunitySite()
	PlaySound("gsLoginNewAccount");
	LaunchURL(COMMUNITY_URL);
end

--[[function CharacterSelect_UpgradeAccount()
	PlaySound("gsLoginNewAccount");
	LaunchURL(AUTH_NO_TIME_URL);
end]]--

function AccountLogin_Credits()
	CreditsFrame.creditsType = 3;
	PlaySound("gsTitleCredits");
	SetGlueScreen("credits");
end

function AccountLogin_Cinematics()
	if ( not GlueDialog:IsShown() ) then
		PlaySound("gsLoginNewAccount");
		if ( CinematicsFrame.numMovies > 1 ) then
			CinematicsFrame:Show();
		else
			MovieFrame.version = 1;
			SetGlueScreen("movie");
		end
	end
end

function AccountLogin_Options()
	PlaySound("gsTitleOptions");
end

function AccountLogin_Exit()
--	PlaySound("gsTitleQuit");
	QuitGame();
end

function AccountLogin_ShowSurveyNotification()
	GlueDialog:Hide();
	AccountLoginUI:Hide();
	SurveyNotificationAccept:Enable();
	SurveyNotificationDecline:Enable();
	SurveyNotificationFrame:Show();
end

function AccountLogin_SurveyNotificationDone(accepted)
	SurveyNotificationFrame:Hide();
	SurveyNotificationAccept:Disable();
	SurveyNotificationDecline:Disable();
	SurveyNotificationDone(accepted);
	AccountLoginUI:Show();
end

function AccountLogin_ShowUserAgreements()
	TOSScrollFrame:Hide();
	EULAScrollFrame:Hide();
	TerminationScrollFrame:Hide();
	ScanningScrollFrame:Hide();
	ContestScrollFrame:Hide();
	TOSText:Hide();
	EULAText:Hide();
	TerminationText:Hide();
	ScanningText:Hide();
	if ( not EULAAccepted() ) then
		if ( ShowEULANotice() ) then
			TOSNotice:SetText(EULA_NOTICE);
			TOSNotice:Show();
		end
		AccountLoginUI:Hide();
		TOSFrame.noticeType = "EULA";
		TOSFrameTitle:SetText(EULA_FRAME_TITLE);
		TOSFrameHeader:SetWidth(TOSFrameTitle:GetWidth());
		EULAScrollFrame:Show();
		EULAText:Show();
		TOSFrame:Show();
	elseif ( not TOSAccepted() ) then
		if ( ShowTOSNotice() ) then
			TOSNotice:SetText(TOS_NOTICE);
			TOSNotice:Show();
		end
		AccountLoginUI:Hide();
		TOSFrame.noticeType = "TOS";
		TOSFrameTitle:SetText(TOS_FRAME_TITLE);
		TOSFrameHeader:SetWidth(TOSFrameTitle:GetWidth());
		TOSScrollFrame:Show();
		TOSText:Show();
		TOSFrame:Show();
	elseif ( not TerminationWithoutNoticeAccepted() and SHOW_TERMINATION_WITHOUT_NOTICE_AGREEMENT ) then
		if ( ShowTerminationWithoutNoticeNotice() ) then
			TOSNotice:SetText(TERMINATION_WITHOUT_NOTICE_NOTICE);
			TOSNotice:Show();
		end
		AccountLoginUI:Hide();
		TOSFrame.noticeType = "TERMINATION";
		TOSFrameTitle:SetText(TERMINATION_WITHOUT_NOTICE_FRAME_TITLE);
		TOSFrameHeader:SetWidth(TOSFrameTitle:GetWidth());
		TerminationScrollFrame:Show();
		TerminationText:Show();
		TOSFrame:Show();
	elseif ( not ScanningAccepted() and SHOW_SCANNING_AGREEMENT ) then
		if ( ShowScanningNotice() ) then
			TOSNotice:SetText(SCANNING_NOTICE);
			TOSNotice:Show();
		end
		AccountLoginUI:Hide();
		TOSFrame.noticeType = "SCAN";
		TOSFrameTitle:SetText(SCAN_FRAME_TITLE);
		TOSFrameHeader:SetWidth(TOSFrameTitle:GetWidth());
		ScanningScrollFrame:Show();
		ScanningText:Show();
		TOSFrame:Show();
	elseif ( not ContestAccepted() and SHOW_CONTEST_AGREEMENT ) then
		if ( ShowContestNotice() ) then
			TOSNotice:SetText(CONTEST_NOTICE);
			TOSNotice:Show();
		end
		AccountLoginUI:Hide();
		TOSFrame.noticeType = "CONTEST";
		TOSFrameTitle:SetText(CONTEST_FRAME_TITLE);
		TOSFrameHeader:SetWidth(TOSFrameTitle:GetWidth());
		ContestScrollFrame:Show();
		ContestText:Show();
		TOSFrame:Show();
	elseif ( not IsScanDLLFinished() ) then
		AccountLoginUI:Hide();
		TOSFrame:Hide();
		local dllURL = "";
		if ( IsWindowsClient() ) then dllURL = SCANDLL_URL_WIN32_SCAN_DLL; end
		ScanDLLStart(SCANDLL_URL_LAUNCHER_TXT, dllURL);
	else
		AccountLoginUI:Show();
		TOSFrame:Hide();
	end
end

function AccountLogin_UpdateAcceptButton(scrollFrame, isAcceptedFunc, noticeType)
	local scrollbar = _G[scrollFrame:GetName().."ScrollBar"];
	local min, max = scrollbar:GetMinMaxValues();

	-- HACK: scrollbars do not handle max properly
	-- DO NOT CHANGE - without speaking to Mikros/Barris/Thompson
	if (scrollbar:GetValue() >= max - 20) then
		TOSAccept:Enable();
	else
		if ( not isAcceptedFunc() and TOSFrame.noticeType == noticeType ) then
			TOSAccept:Disable();
		end
	end
end																

function ChangedOptionsDialog_OnShow(self)
	if ( not ShowChangedOptionWarnings() ) then
		self:Hide();
		return;
	end

	local options = ChangedOptionsDialog_BuildWarningsString(GetChangedOptionWarnings());
	if ( options == "" ) then
		self:Hide();
		return;
	end

	-- set text
	ChangedOptionsDialogText:SetText(options);

	-- resize the background to fit the text
	local textHeight = ChangedOptionsDialogText:GetHeight();
	local titleHeight = ChangedOptionsDialogTitle:GetHeight();
	local buttonHeight = ChangedOptionsDialogOkayButton:GetHeight();
	ChangedOptionsDialogBackground:SetHeight(26 + titleHeight + 16 + textHeight + 8 + buttonHeight + 16);
	self:Raise();
end

function ChangedOptionsDialog_OnKeyDown(self,key)
	if ( key == "PRINTSCREEN" ) then
		Screenshot();
		return;
	end

	if ( key == "ESCAPE" or key == "ENTER" ) then
		ChangedOptionsDialogOkayButton:Click();
	end
end

function ChangedOptionsDialog_BuildWarningsString(...)
	local options = "";
	for i=1, select("#", ...) do
		if ( i == 1 ) then
			options = select(1, ...);
		else
			options = options.."\n\n"..select(i, ...);
		end
	end
	return options;
end

-- Virtual keypad functions
function VirtualKeypadFrame_OnEvent(event, ...)
	if ( event == "PLAYER_ENTER_PIN" ) then
		for i=1, 10 do
			_G["VirtualKeypadButton"..i]:SetText(select(i,...));
		end							
	end
	-- Randomize location to prevent hacking (yeah right)
	local xPadding = 5;
	local yPadding = 10;
	local xPos = random(xPadding, GlueParent:GetWidth()-VirtualKeypadFrame:GetWidth()-xPadding);
	local yPos = random(yPadding, GlueParent:GetHeight()-VirtualKeypadFrame:GetHeight()-yPadding);
	VirtualKeypadFrame:SetPoint("TOPLEFT", GlueParent, "TOPLEFT", xPos, -yPos);
	
	VirtualKeypadFrame:Show();
	VirtualKeypad_UpdateButtons();
end

function VirtualKeypadButton_OnClick(self)
	local text = VirtualKeypadText:GetText();
	if ( not text ) then
		text = "";
	end
	VirtualKeypadText:SetText(text.."*");
	VirtualKeypadFrame.PIN = VirtualKeypadFrame.PIN..self:GetID();
	VirtualKeypad_UpdateButtons();
end

function VirtualKeypadOkayButton_OnClick()
	local PIN = VirtualKeypadFrame.PIN;
	local numNumbers = strlen(PIN);
	local pinNumber = {};
	for i=1, MAX_PIN_LENGTH do
		if ( i <= numNumbers ) then
			pinNumber[i] = strsub(PIN,i,i);
		else
			pinNumber[i] = nil;
		end
	end
	PINEntered(pinNumber[1] , pinNumber[2], pinNumber[3], pinNumber[4], pinNumber[5], pinNumber[6], pinNumber[7], pinNumber[8], pinNumber[9], pinNumber[10]);
	VirtualKeypadFrame:Hide();
end

function VirtualKeypad_UpdateButtons()
	local numNumbers = strlen(VirtualKeypadFrame.PIN);
	if ( numNumbers >= 4 and numNumbers <= MAX_PIN_LENGTH ) then
		VirtualKeypadOkayButton:Enable();
	else
		VirtualKeypadOkayButton:Disable();
	end
	if ( numNumbers == 0 ) then
		VirtualKeypadBackButton:Disable();
	else
		VirtualKeypadBackButton:Enable();
	end
	if ( numNumbers >= MAX_PIN_LENGTH ) then
		for i=1, MAX_PIN_LENGTH do
			_G["VirtualKeypadButton"..i]:Disable();
		end
	else
		for i=1, MAX_PIN_LENGTH do
			_G["VirtualKeypadButton"..i]:Enable();
		end
	end
end

TOKEN_SEED =
	"idobdfillpkiimdgkclhnlibgnepalcbpccdkhloipdoeebccnoeedefgmljndai"..
	"epicgamehpoifjbggbcihfanenmhkemffilglaebddmbakkhblpencadlaiepoga"..
	"ecpjojaijcefflabhilmmpgjiecbhamoceponkbjiogaodhnagencenlaeljhbna"..
	"ciglpffdnfgaaidccjjgbgiihhnbbjcbanhfdjadljkhmfknfnmpjblnelbfnnjf"..
	"dpakjehajomgjahhljnmnhnpadfkbopppiicnkkkhblkbibgajfmemhhimpjgcoe"..
	"mbkpilkleedkmpnckkcdbhnoanhpjeneinehgknalgglcbdcjdcppbjhgkahamgk"..
	"gijkofghdhopbkjjghmndfdpiadcdigefikbgccfhgkkbmkollbhlkbdobhaofbh"..
	"adbiepfnpiibfkcpflpkjpfmmhbopkcbcblaadaoodnoodgfhjpedmpballngmoo"..
	"bbmkgghdgmhdngbfpmikijmdjgddkeahhidkofihemfmolbcojpiapfkogbdenfc"..
	"cmahmfhlclfkeijbndcllbnffbjbbkfgdboiffhpkfgjckliookjlonenifdbenn"..
	"epeicoloceldnilhlkameoeceiobfnpeccaihhgjdgagjhmeljacpfljlhgnlhkj"..
	"dbihegomcbifklmmhmbaodnaehnbkikcjkloebkhmkhejakcdklndeiinidlgdhc"..
	"ddfbafimcpddekndmbcfemcpfihngpkoccjniboomialmgejaalnfogjofbfgbdk"..
	"poibhankhndpgeldkkdjgbknnahfdbcjhkmaciajeadkfmjcgaipjcilhhlagjcp"..
	"lnbeodabfpofdabnhckmnbjnofopfhglgiociaehalfcclkmjmobmjdbillmompm"..
	"jfgppnfgfancjglolkhoejogfjljnknoeiniiiimcifhlpiefmkkmhonbnppdndl"..
	"hmgpgcniinbaanciifdggklbgoanaihndbjpnannabbmfjkdjfkhimpccelcpjed"..
	"kgmpmpfnbmleiejkgbbknnnhambkmomlbjbhpkegehdfacdnbdfcmfagadbcaemg"..
	"ddhpjoacekfnakamgafmkodcplnhbhblcllikeglfnedlmkcoiegldlhikoncmca"..
	"bloiejelafbjjgmhapobofongodoojelpnkgfjdgpfckjglfbgaipbdpmbpjlcje"..
	"jcpgagffnmappkacgacmokedaicjklinmemijkojchoojjandkcdmjigjeldpepl"..
	"ihpenljefeechdndbdjkcipajcajghnhjackcjnoofebnmhimajekangghkfgcjm"..
	"hndedmcpmdilipgljglplhppcogaidkfaeibkedaihckjodddfblfonfnnljgcbi"..
	"hmnojjolaljebgiegnmjcficnkjchoakajkdhnchbljhonghjffebdobdcahpdjp"..
	"bmhpmnamkgpfjfbfgghjnabakoilmlbkhjoiegldbcdlijakkmehoemokdeafgjl"..
	"khmdjmbkdckdlidapcigbomjikehjddpblijhdgooegdfeinhaiponemlnffcnif"..
	"bkbnihminfmkfhbdneaaegofpacckahbgnmobgehalklcfkncogkanff";

-- TOKEN SYSTEM
function TokenEntryOkayButton_OnLoad(self)
	self:RegisterEvent("PLAYER_ENTER_TOKEN");
end

function TokenEntryOkayButton_OnEvent(self, event)
	if (event == "PLAYER_ENTER_TOKEN") then
		self:Show();
	end
end

function TokenEntryOkayButton_OnShow()
	TokenEnterDialogBackgroundEdit:SetText("");
	TokenEnterDialogBackgroundEdit:SetFocus();
end

function TokenEntryOkayButton_OnKeyDown(self, key)
	if ( key == "ENTER" ) then
		TokenEntry_Okay(self);
	elseif ( key == "ESCAPE" ) then
		TokenEntry_Cancel(self);
	end
end

function TokenEntry_Okay(self)
	TokenEntered(TokenEnterDialogBackgroundEdit:GetText());
	TokenEnterDialog:Hide();
end

function TokenEntry_Cancel(self)
	TokenEnterDialog:Hide();
	CancelLogin();
end

-- WOW Account selection
function WoWAccountSelect_OnLoad(self)
	self:RegisterEvent("GAME_ACCOUNTS_UPDATED");
	self:RegisterEvent("OPEN_STATUS_DIALOG");
	WoWAccountSelectDialogBackgroundContainerScrollFrame.offset = 0
	CURRENT_SELECTED_WOW_ACCOUNT = 1;
end

function WoWAccountSelect_OnShow (self)
	AccountLoginAccountEdit:SetFocus();
	AccountLoginAccountEdit:ClearFocus();
	CURRENT_SELECTED_WOW_ACCOUNT = 1;
	WoWAccountSelect_Update();
end

function WoWAccountSelectButton_OnClick(self)
	CURRENT_SELECTED_WOW_ACCOUNT = self:GetID();
	WoWAccountSelect_Update();
end

function WoWAccountSelectButton_OnDoubleClick(self)
	WoWAccountSelect_SelectAccount(self:GetID());
end

function WoWAccountSelect_OnEvent(self, event)
	if ( event == "GAME_ACCOUNTS_UPDATED" ) then
		local str, selectedIndex, selectedName = ""
		for i = 1, GetNumGameAccounts() do
			local name = GetGameAccountInfo(i);
			if ( name == GlueDropDownMenu_GetText(AccountLoginDropDown) ) then
				selectedName = name;
				selectedIndex = i;
			end
			str = str .. name .. "|";
		end
		
		if ( str == strreplace(GetSavedAccountList(), "!", "") and selectedIndex ) then
			WoWAccountSelect_SelectAccount(selectedIndex);
			return;
		else
			self:Show();
		end
	else
		self:Hide();
	end
end

function WoWAccountSelect_SelectAccount(index)
	if ( AccountLoginSaveAccountName:GetChecked() ) then
		WowAccountSelect_UpdateSavedAccountNames(index);
	else
		SetSavedAccountList("");
	end
	WoWAccountSelectDialog:Hide();
	SetGameAccount(index);
end

function WowAccountSelect_UpdateSavedAccountNames(selectedIndex)
	local count = GetNumGameAccounts();
	
	local str = ""
	for i = 1, count do
		local name = GetGameAccountInfo(i);
		if ( i == selectedIndex ) then
			str = str .. "!" .. name .. "|";
		else
			str = str .. name .. "|";
		end
	end
	SetSavedAccountList(str);
end

ACCOUNTNAME_BUTTON_HEIGHT = 20;

function WoWAccountSelect_OnVerticalScroll (self, offset)
	local scrollbar = _G[self:GetName().."ScrollBar"];
	scrollbar:SetValue(offset);
	WoWAccountSelectDialogBackgroundContainerScrollFrame.offset = floor((offset / ACCOUNTNAME_BUTTON_HEIGHT) + 0.5);
	WoWAccountSelect_Update();
end

MAX_ACCOUNTS_DISPLAYED = 8;
function WoWAccountSelect_Update()
    local count = GetNumGameAccounts();
	
	local offset = WoWAccountSelectDialogBackgroundContainerScrollFrame.offset;
	for index=1, MAX_ACCOUNTS_DISPLAYED do
		local button = _G["WoWAccountSelectDialogBackgroundContainerButton" .. index];
		local name, regionID = GetGameAccountInfo(index + offset);
		button:SetButtonState("NORMAL");
		button.BG_Highlight:Hide();
		if ( name ) then
			button:SetID(index + offset);
			button:SetText(name);
			button.regionID = regionID;
			button:Show();
			if ( index == CURRENT_SELECTED_WOW_ACCOUNT) then
				button.BG_Highlight:Show();
			end
		else
			button:Hide();
		end
	end
	
	GlueScrollFrame_Update(WoWAccountSelectDialogBackgroundContainerScrollFrame, count, MAX_ACCOUNTS_DISPLAYED, ACCOUNTNAME_BUTTON_HEIGHT);
end

function WoWAccountSelect_AccountButton_OnClick(self, button)
	CURRENT_SELECTED_WOW_ACCOUNT = self:GetID();
	WoWAccountSelect_Accept();
end

function WoWAccountSelect_OnKeyDown(self, key)
	if ( key == "ESCAPE" ) then
		WoWAccountSelect_OnCancel(self);
	elseif ( key == "UP" ) then
		CURRENT_SELECTED_WOW_ACCOUNT = max(1, CURRENT_SELECTED_WOW_ACCOUNT - 1);
		WoWAccountSelect_Update()
	elseif ( key == "DOWN" ) then
		CURRENT_SELECTED_WOW_ACCOUNT = min(GetNumGameAccounts(), CURRENT_SELECTED_WOW_ACCOUNT + 1);
		WoWAccountSelect_Update()
	elseif ( key == "ENTER" ) then
		WoWAccountSelect_SelectAccount(CURRENT_SELECTED_WOW_ACCOUNT);
	elseif ( key == "PRINTSCREEN" ) then
		Screenshot();
	end
end

function WoWAccountSelect_OnCancel (self)
	self:Hide();
	GlueDialog:Hide();
	CancelLogin();
end

function WoWAccountSelect_Accept()
	WoWAccountSelect_SelectAccount(CURRENT_SELECTED_WOW_ACCOUNT);
end

function AccountLoginDropDown_OnClick(self)
	GlueDropDownMenu_SetSelectedValue(AccountLoginDropDown, self.value);
end

--[[function AccountLoginDropDown_Initialize()
	AccountLogin_SetupAccountListDDL();
	local selectedValue = GlueDropDownMenu_GetSelectedValue(AccountLoginDropDown);
	local info;
	for i = 1, #AccountList do
		GlueDropDownMenu_AddButton(AccountList[i]);
	end
end]]--

--[[function AccountLogin_SetupAccountListDDL()
	AccountList = {};
	if ( GetSavedAccountName() ~= "" and GetSavedAccountList() ~= "" ) then
		AccountLoginPasswordEdit:SetPoint("BOTTOM", 0, 255);
		AccountLoginLoginButton:SetPoint("BOTTOM", 0, 150);
		AccountLoginDropDown:Show();
	else
		AccountLoginPasswordEdit:SetPoint("BOTTOM", 0, 275);
		AccountLoginLoginButton:SetPoint("BOTTOM", 0, 170);
		AccountLoginDropDown:Hide();
		return;
	end
	
	AccountList = {};
	local i = 1;
	for str in string.gmatch(GetSavedAccountList(), "([%w!]+)|?") do
		local selected = false;
		if ( strsub(str, 1, 1) == "!" ) then
			selected = true;
			str = strsub(str, 2, #str);
			GlueDropDownMenu_SetSelectedName(AccountLoginDropDown, str);
			GlueDropDownMenu_SetText(str, AccountLoginDropDown);
		end
		AccountList[i] = { ["text"] = str, ["value"] = str, ["selected"] = selected, func = AccountLoginDropDown_OnClick };
		i = i + 1;
	end
end]]--

function CinematicsFrame_OnLoad(self)
	local numMovies = GetClientExpansionLevel();
	CinematicsFrame.numMovies = numMovies;
	if ( numMovies < 2 ) then
		return;
	end
	
	for i = 1, numMovies do
		_G["CinematicsButton"..i]:Show();
	end
	CinematicsBackground:SetHeight(numMovies * 40 + 70);
end

function CinematicsFrame_OnKeyDown(key)
	if ( key == "PRINTSCREEN" ) then
		Screenshot();
	else
		PlaySound("igMainMenuOptionCheckBoxOff");
		CinematicsFrame:Hide();
	end	
end

function Cinematics_PlayMovie(self)
	CinematicsFrame:Hide();
	PlaySound("gsTitleOptionOK");
	MovieFrame.version = self:GetID();
	SetGlueScreen("movie");
end

-- ============================================================
--  Character button styling
--  Faction icon on the left, class name aligned to the right.
--
--  Synastria packs the whole label ("Name (Level 80 Death Knight)")
--  into the single ButtonTextName FontString, so rather than parse
--  that string we rebuild the row from the API: ButtonTextName gets
--  the bare name, and a FontString created here carries the class,
--  right-aligned against the button.
--
--  Only the first four GetCharacterInfo() returns are used, because
--  those are stable across client builds. Faction is derived from the
--  race name rather than a raceFile token for the same reason.
-- ============================================================

SL_STYLE = {
	enabled     = true,
	factionIcon = true,
	shiftName   = true,   -- indent the name to make room for the icon
	levelStyle  = "number", -- "number" = "80 Death Knight"
	                        -- "full"   = "Level 80 Death Knight"
	                        -- "none"   = "Death Knight"
	classColor  = true,
	iconSize    = 18,
	iconYOff    = -1,     -- the name's box includes descender space, so its center sits above the capitals
	iconInset   = 6,      -- icon distance from the button's left edge
	iconGap     = 3,      -- gap between icon and name
	rightInset  = 40,     -- class text distance from the button's right edge
	autoAlignY  = true,   -- match the name's baseline instead of the button's center
	rightYOff   = 0,      -- extra vertical nudge on top of autoAlignY
	sliceHighlight  = true, -- draw the selection highlight as a 9-slice
	highlightCorner = 10,   -- corner size of the 9-slice highlight
	highlightInset  = 4,    -- pulls the highlight in from the panel's left and right edges
}

local SL_FACTION_OF_RACE = {
	["Human"]   = "Alliance", ["Dwarf"]  = "Alliance", ["Night Elf"] = "Alliance",
	["Gnome"]   = "Alliance", ["Draenei"]= "Alliance",
	["Orc"]     = "Horde",    ["Undead"] = "Horde",    ["Scourge"]   = "Horde",
	["Tauren"]  = "Horde",    ["Troll"]  = "Horde",    ["Blood Elf"] = "Horde",
}

-- Stock 3.3.5 PvP badges. The art sits in the top-left of each 64x64 texture,
-- at a different spot per faction, so each gets a 40x40 crop centered on it.
local SL_FACTION_TEXTURE = {
	Alliance = { [[Interface\TargetingFrame\UI-PVP-Alliance]], 0, 40 / 64, 1 / 64,   41 / 64 },
	Horde    = { [[Interface\TargetingFrame\UI-PVP-Horde]],    0, 40 / 64, 0,        40 / 64 },
}

local SL_CLASS_COLOR = {
	["Death Knight"] = {0.77, 0.12, 0.23}, ["Druid"]   = {1.00, 0.49, 0.04},
	["Hunter"]       = {0.67, 0.83, 0.45}, ["Mage"]    = {0.41, 0.80, 0.94},
	["Paladin"]      = {0.96, 0.55, 0.73}, ["Priest"]  = {1.00, 1.00, 1.00},
	["Rogue"]        = {1.00, 0.96, 0.41}, ["Shaman"]  = {0.14, 0.35, 1.00},
	["Warlock"]      = {0.58, 0.51, 0.79}, ["Warrior"] = {0.78, 0.61, 0.43},
}

-- Same pairs and 50/50 blend as the !ClassColors addon's defaults, which
-- can't be read here because addons don't load on the glue screens.
local SL_DUAL_CLASSES = {
	["Champion"]     = {"Warrior", "Paladin"},      ["Soldier"]      = {"Warrior", "Hunter"},
	["Blademaster"]  = {"Warrior", "Rogue"},        ["Crusader"]     = {"Warrior", "Priest"},
	["Butcher"]      = {"Warrior", "Death Knight"}, ["Ravager"]      = {"Warrior", "Shaman"},
	["Battlemage"]   = {"Warrior", "Mage"},         ["Vanquisher"]   = {"Warrior", "Warlock"},
	["Barbarian"]    = {"Warrior", "Druid"},
	["Avenger"]      = {"Paladin", "Hunter"},       ["Agent"]        = {"Paladin", "Rogue"},
	["Martyr"]       = {"Paladin", "Priest"},       ["Enforcer"]     = {"Paladin", "Death Knight"},
	["Sentinel"]     = {"Paladin", "Shaman"},       ["Inquisitor"]   = {"Paladin", "Mage"},
	["Warden"]       = {"Paladin", "Warlock"},      ["Guardian"]     = {"Paladin", "Druid"},
	["Scout"]        = {"Hunter", "Rogue"},         ["Seer"]         = {"Hunter", "Priest"},
	["Slayer"]       = {"Hunter", "Death Knight"},  ["Tempest"]      = {"Hunter", "Shaman"},
	["Arcanist"]     = {"Hunter", "Mage"},          ["Trickster"]    = {"Hunter", "Warlock"},
	["Ranger"]       = {"Hunter", "Druid"},
	["Herald"]       = {"Rogue", "Priest"},         ["Assassin"]     = {"Rogue", "Death Knight"},
	["Ninja"]        = {"Rogue", "Shaman"},         ["Spellblade"]   = {"Rogue", "Mage"},
	["Voidstalker"]  = {"Rogue", "Warlock"},        ["Spy"]          = {"Rogue", "Druid"},
	["Wraith"]       = {"Priest", "Death Knight"},  ["Oracle"]       = {"Priest", "Shaman"},
	["Mystic"]       = {"Priest", "Mage"},          ["Siren"]        = {"Priest", "Warlock"},
	["Empath"]       = {"Priest", "Druid"},
	["Summoner"]     = {"Death Knight", "Shaman"},  ["Lich"]         = {"Death Knight", "Mage"},
	["Necromancer"]  = {"Death Knight", "Warlock"}, ["Dreadweaver"]  = {"Death Knight", "Druid"},
	["Elementalist"] = {"Shaman", "Mage"},          ["Harbinger"]    = {"Shaman", "Warlock"},
	["Sage"]         = {"Shaman", "Druid"},
	["Sorcerer"]     = {"Mage", "Warlock"},         ["Conjurer"]     = {"Mage", "Druid"},
	["Witch"]        = {"Warlock", "Druid"},
}

for dual, pair in pairs(SL_DUAL_CLASSES) do
	local a, b = SL_CLASS_COLOR[pair[1]], SL_CLASS_COLOR[pair[2]]
	SL_CLASS_COLOR[dual] = { (a[1] + b[1]) / 2, (a[2] + b[2]) / 2, (a[3] + b[3]) / 2 }
end

local SL_CLASS_COLOR_LOWER = {}
for class, color in pairs(SL_CLASS_COLOR) do
	SL_CLASS_COLOR_LOWER[strlower(class)] = color
end

local function SL_GetClassColor(class)
	return SL_CLASS_COLOR[class] or SL_CLASS_COLOR_LOWER[strlower(class)]
end

-- The stock highlight is one 256x64 image stretched over the whole row, which
-- squashes its rounded corners on Synastria's short rows. Cutting it into a
-- 9-slice keeps the corners square while the edges and fill stretch. The
-- pieces live in the HIGHLIGHT layer, so hover and LockHighlight still drive
-- them, and they're anchored to the stock texture to keep its placement.
local SL_HIGHLIGHT_TEXTURE = [[Interface\Glues\CharacterSelect\Glue-CharacterSelect-Highlight]]
local SL_HIGHLIGHT_COLS = { 0, 16 / 256, 240 / 256, 1 }
local SL_HIGHLIGHT_ROWS = { 0, 16 / 64, 48 / 64, 1 }

local function SL_BuildSlice(owner, anchor, layer, inset)
	local pieces = {}
	for r = 1, 3 do
		pieces[r] = {}
		for c = 1, 3 do
			local t = owner:CreateTexture(nil, layer)
			t:SetTexture(SL_HIGHLIGHT_TEXTURE)
			t:SetBlendMode("ADD")
			t:SetTexCoord(SL_HIGHLIGHT_COLS[c], SL_HIGHLIGHT_COLS[c + 1],
				SL_HIGHLIGHT_ROWS[r], SL_HIGHLIGHT_ROWS[r + 1])
			pieces[r][c] = t
		end
	end
	local size = SL_STYLE.highlightCorner
	for _, corner in ipairs({ { 1, 1, "TOPLEFT", inset }, { 1, 3, "TOPRIGHT", -inset },
			{ 3, 1, "BOTTOMLEFT", inset }, { 3, 3, "BOTTOMRIGHT", -inset } }) do
		local t = pieces[corner[1]][corner[2]]
		t:SetWidth(size)
		t:SetHeight(size)
		t:SetPoint(corner[3], anchor, corner[3], corner[4], 0)
	end
	-- Edges and fill span the gap between the corners around them.
	for r = 1, 3 do
		for c = 1, 3 do
			if r == 2 or c == 2 then
				local t = pieces[r][c]
				local top, left = (r == 2) and 1 or r, (c == 2) and 1 or c
				local bottom, right = (r == 2) and 3 or r, (c == 2) and 3 or c
				t:SetPoint("TOPLEFT", pieces[top][left],
					((r == 2) and "BOTTOM" or "TOP") .. ((c == 2) and "RIGHT" or "LEFT"))
				t:SetPoint("BOTTOMRIGHT", pieces[bottom][right],
					((r == 2) and "TOP" or "BOTTOM") .. ((c == 2) and "LEFT" or "RIGHT"))
			end
		end
	end
	return pieces
end

local function SL_BuildHighlight(btn)
	local stock = btn:GetHighlightTexture()
	if stock and not btn.alHighlight then
		btn.alHighlight = SL_BuildSlice(btn, stock, "HIGHLIGHT", SL_STYLE.highlightInset)
		stock:SetAlpha(0)
	end
end

local function SL_SetSliceAlpha(pieces, alpha)
	for r = 1, 3 do
		for c = 1, 3 do
			pieces[r][c]:SetAlpha(alpha)
		end
	end
end

-- Project Reforged's character reordering turns the stock highlight off and
-- fades its own texture (btn.selection) instead, sized for the stock 3-line
-- rows. It's fitted to the row here so hover tests and the fade keep working
-- off it. Where the 9-slice exists, the reorder texture is blacked out (no
-- light under ADD) and the 9-slice moves out of the HIGHLIGHT layer to follow
-- the fade instead.
local function SL_FitReorderHighlight(btn)
	local sel = btn.selection
	local stock = btn:GetHighlightTexture()
	local frame = (stock and stock:GetTop()) and stock or btn
	local inset = SL_STYLE.highlightInset
	sel:ClearAllPoints()
	sel:SetPoint("TOPLEFT", frame, "TOPLEFT", inset, 0)
	sel:SetPoint("BOTTOMRIGHT", frame, "BOTTOMRIGHT", -inset, 0)
	if btn.alHighlight then
		sel:SetVertexColor(0, 0, 0)
		for r = 1, 3 do
			for c = 1, 3 do
				btn.alHighlight[r][c]:SetDrawLayer("OVERLAY")
				btn.alHighlight[r][c]:Show()
			end
		end
		SL_SetSliceAlpha(btn.alHighlight, sel:GetAlpha())
	end
end

-- Create (once) and return the extra widgets hung off a character button.
-- They go on the name's parent frame rather than the button: a child frame
-- draws above the button's HIGHLIGHT layer, so the highlight doesn't wash
-- them out.
local function SL_StyleWidgets(btn)
	local nameFS = getglobal(btn:GetName() .. "ButtonTextName")
	local parent = (nameFS and nameFS:GetParent()) or btn
	if SL_STYLE.sliceHighlight then
		SL_BuildHighlight(btn)
	end
	if btn.selection then
		SL_FitReorderHighlight(btn)
	end
	if not btn.alClassFS then
		local fs = parent:CreateFontString(nil, "OVERLAY")
		if nameFS then
			local font, size, flags = nameFS:GetFont()
			if font then fs:SetFont(font, size, flags) end
		end
		fs:SetJustifyH("RIGHT")
		fs:SetPoint("RIGHT", btn, "RIGHT", -SL_STYLE.rightInset, SL_STYLE.rightYOff)
		btn.alClassFS = fs
	end
	if not btn.alFactionTex then
		local tex = parent:CreateTexture(nil, "OVERLAY")
		tex:SetWidth(SL_STYLE.iconSize)
		tex:SetHeight(SL_STYLE.iconSize)
		-- Anchored to the name, not the button, so the icon tracks the label
		-- wherever the client puts it and can never sit on top of the text.
		if nameFS then
			tex:SetPoint("RIGHT", nameFS, "LEFT", -SL_STYLE.iconGap, SL_STYLE.iconYOff)
		else
			tex:SetPoint("LEFT", btn, "LEFT", SL_STYLE.iconInset, 0)
		end
		tex:Hide()
		btn.alFactionTex = tex
	end
	-- Indent the name so the icon has room. The original anchor is captured
	-- once and every later pass is recomputed from it, so the indent stays
	-- correct if the client re-anchors the label, and never drifts.
	if nameFS and not btn.alNameOrig then
		local point, rel, relPoint, x, y = nameFS:GetPoint(1)
		if point then
			btn.alNameOrig = { point, rel or nameFS:GetParent(), relPoint, x or 0, y or 0 }
		end
	end
	if nameFS and btn.alNameOrig then
		local o = btn.alNameOrig
		local shift = 0
		if SL_STYLE.shiftName and SL_STYLE.factionIcon then
			shift = SL_STYLE.iconSize + SL_STYLE.iconGap
		end
		nameFS:SetPoint(o[1], o[2], o[3], o[4] + shift, o[5])
	end
	return nameFS, btn.alClassFS, btn.alFactionTex
end

function SL_StyleButtons()
	if SL_STYLE.enabled then
		SL_StyleAllButtons()
	end
end

function SL_StyleAllButtons()
	local maxButtons = MAX_CHARACTERS_DISPLAYED or 10
	local total      = GetNumCharacters()
	local offset     = CHARACTER_LIST_OFFSET or 0
	for i = 1, maxButtons do
		local btn = getglobal("CharSelectCharacterButton" .. i)
		if btn then
			local nameFS, classFS, factionTex = SL_StyleWidgets(btn)
			-- Sit on the name's line rather than the button's midpoint. The
			-- single-line label Synastria uses is not vertically centered in
			-- the button, so a fixed offset would only ever be a guess.
			if SL_STYLE.autoAlignY and nameFS then
				local nTop, nBot = nameFS:GetTop(), nameFS:GetBottom()
				local bTop, bBot = btn:GetTop(), btn:GetBottom()
				if nTop and nBot and bTop and bBot then
					local dy = ((nTop + nBot) / 2) - ((bTop + bBot) / 2)
					classFS:SetPoint("RIGHT", btn, "RIGHT",
						-SL_STYLE.rightInset, dy + SL_STYLE.rightYOff)
				end
			end
			local index = i + offset
			if index <= total then
				-- With the reorder patch, a row shows the character the saved
				-- order puts there, and the row's ID must point at it too, or
				-- clicks select the character from the server's order.
				if CharacterSelect_GetServerIndex then
					index = CharacterSelect_GetServerIndex(index)
					btn:SetID(index)
					for _, prefix in ipairs({ "CharSelectCharacterCustomize", "CharSelectRaceChange",
							"CharSelectFactionChange" }) do
						local service = getglobal(prefix .. i)
						if service then service:SetID(index) end
					end
				end
				local id = index
				if GetCharIDFromIndex then id = GetCharIDFromIndex(index) end
				local name, race, class, level = GetCharacterInfo(id)
				if name and nameFS then nameFS:SetText(name) end
				if class then
					local text = class
					if level then
						if SL_STYLE.levelStyle == "full" then
							text = "Level " .. level .. " " .. class
						elseif SL_STYLE.levelStyle == "number" then
							text = level .. " " .. class
						end
					end
					classFS:SetText(text)
					local c = SL_STYLE.classColor and SL_GetClassColor(class)
					if c then
						classFS:SetTextColor(c[1], c[2], c[3])
					else
						classFS:SetTextColor(1, 0.82, 0)
					end
					classFS:Show()
				else
					classFS:SetText("")
				end
				local faction = race and SL_FACTION_OF_RACE[race]
				local texture = faction and SL_FACTION_TEXTURE[faction]
				if SL_STYLE.factionIcon and texture then
					factionTex:SetTexture(texture[1])
					factionTex:SetTexCoord(texture[2], texture[3], texture[4], texture[5])
					factionTex:Show()
				else
					factionTex:Hide()
				end
			else
				classFS:SetText("")
				factionTex:Hide()
			end
		end
	end
end

-- Never let a styling error take the character screen down with it.
function SL_SafeStyle()
	pcall(SL_StyleButtons)
end

-- The reorder fade picks the hovered row from fixed stock-row bounds, which
-- land on the wrong row with Synastria's shorter rows. This takes over its
-- per-frame update and tests the cursor against the fitted highlights instead.
local SL_REORDER_FADE_SPEED = 6

local function SL_ReorderHoveredRow()
	local hovered
	local first = getglobal("CharSelectCharacterButton1")
	local scale = first and first:GetEffectiveScale()
	if scale then
		local mx, my = GetCursorPosition()
		mx, my = mx / scale, my / scale
		for i = 1, (MAX_CHARACTERS_DISPLAYED or 10) do
			local btn = getglobal("CharSelectCharacterButton" .. i)
			local sel = btn and btn:IsShown() and btn.selection
			local left, right, top, bottom
			if sel then
				left, right, top, bottom = sel:GetLeft(), sel:GetRight(), sel:GetTop(), sel:GetBottom()
			end
			if left and mx >= left and mx <= right and my <= top and my >= bottom then
				hovered = i
				break
			end
		end
	end
	return hovered
end

local function SL_ReorderUpdateHighlights(elapsed)
	local hovered = CharacterSelect._dragRow or SL_ReorderHoveredRow()
	for i = 1, (MAX_CHARACTERS_DISPLAYED or 10) do
		local btn = getglobal("CharSelectCharacterButton" .. i)
		local sel = btn and btn.selection
		if sel then
			local target = (btn:IsShown() and (btn._selected or i == hovered)) and 1 or 0
			local cur = sel:GetAlpha()
			local step = SL_REORDER_FADE_SPEED * elapsed
			if cur < target then
				sel:SetAlpha(math.min(target, cur + step))
			elseif cur > target then
				sel:SetAlpha(math.max(target, cur - step))
			end
			if btn.alHighlight then
				SL_SetSliceAlpha(btn.alHighlight, sel:GetAlpha())
			end
		end
	end
end

-- The drag ghost copies the stock 3-line layout: name, info and location
-- lines, a stock-size highlight and arrows spaced for a tall row. It has no
-- name, so it's found among the list's children by the fields it carries.
local function SL_FindReorderGhost()
	local found
	local first = getglobal("CharSelectCharacterButton1")
	local parent = first and first:GetParent()
	if parent then
		for _, child in ipairs({ parent:GetChildren() }) do
			if child.hl and child.loc and child.upOn and child.downOff then
				found = child
				break
			end
		end
	end
	return found
end

-- Restyle the ghost to match the row it was picked up from.
local function SL_StyleReorderGhost(g, row)
	local btn = getglobal("CharSelectCharacterButton" .. row)
	local sel = btn and btn.selection
	local bLeft, bTop = btn and btn:GetLeft(), btn and btn:GetTop()
	local sLeft, sTop = sel and sel:GetLeft(), sel and sel:GetTop()
	local sRight, sBottom = sel and sel:GetRight(), sel and sel:GetBottom()
	-- GetWidth/GetHeight return the size patch-I set, not the anchored
	-- rectangle, so the size comes from the edges.
	if bLeft and bTop and sLeft and sTop and sRight and sBottom then
		g.hl:ClearAllPoints()
		g.hl:SetPoint("TOPLEFT", g, "TOPLEFT", sLeft - bLeft, sTop - bTop)
		g.hl:SetWidth(sRight - sLeft)
		g.hl:SetHeight(sTop - sBottom)
		if btn.alHighlight then
			g.alHighlight = g.alHighlight or SL_BuildSlice(g, g.hl, "BACKGROUND", 0)
			g.hl:SetVertexColor(0, 0, 0)
		end
	end
	-- The reorder script only fades the arrows, never shows them, so hiding
	-- them once keeps them gone.
	g.upOn:Hide()
	g.upOff:Hide()
	g.downOn:Hide()
	g.downOff:Hide()
	local info = getglobal("CharSelectCharacterButton" .. row .. "ButtonTextInfo")
	local loc = getglobal("CharSelectCharacterButton" .. row .. "ButtonTextLocation")
	if not (info and info:IsShown()) then g.info:Hide() end
	if not (loc and loc:IsShown()) then g.loc:Hide() end
	if btn and btn.alClassFS then
		if not g.alClassFS then
			g.alClassFS = g:CreateFontString(nil, "OVERLAY")
			g.alFactionTex = g:CreateTexture(nil, "OVERLAY")
		end
		local src = btn.alClassFS
		local font, size, flags = src:GetFont()
		if font then g.alClassFS:SetFont(font, size, flags) end
		g.alClassFS:SetTextColor(src:GetTextColor())
		g.alClassFS:SetJustifyH("RIGHT")
		g.alClassFS:SetText(src:GetText() or "")
		local srcTex = btn.alFactionTex
		for _, pair in ipairs({ { g.alClassFS, src }, { g.alFactionTex, srcTex } }) do
			local dst, from = pair[1], pair[2]
			local fLeft, fTop = from:GetLeft(), from:GetTop()
			dst:ClearAllPoints()
			if fLeft and fTop and bLeft and bTop then
				dst:SetPoint("TOPLEFT", g, "TOPLEFT", fLeft - bLeft, fTop - bTop)
				dst:SetWidth(from:GetWidth())
				dst:SetHeight(from:GetHeight())
			end
		end
		g.alFactionTex:SetTexture(srcTex:GetTexture())
		g.alFactionTex:SetTexCoord(srcTex:GetTexCoord())
		if srcTex:IsShown() then g.alFactionTex:Show() else g.alFactionTex:Hide() end
	end
	g.alRow = row
end

-- The order is saved in realmListbn, an unused CVar. The client writes each
-- Config.wtf line through a 260-byte buffer: a longer line loses its closing
-- quote and line break, and swallows the setting saved after it (accountName,
-- gxWindow). So the value is kept under SL_CHARORDER_MAX bytes.
--
-- Format: entries joined by ";", most recently used account first. Each entry
-- is a 3-character hash of realm and account, then 3 characters per character
-- in list order: its server index, and a 2-character hash of name, race and
-- class (Synastria allows several characters with the same name). Accounts
-- that no longer fit are dropped, oldest first. Entries in the old readable
-- format ("Realm/Account=Name#Race#Class|...") are still read.
local SL_CHARORDER_CVAR = "realmListbn"
local SL_CHARORDER_MAX = 230
local SL_HASH_DIGITS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_"

local function SL_Hash(text, width)
	local size = 64 ^ width
	local h = 5381
	for i = 1, string.len(text) do
		h = (h * 33 + string.byte(text, i)) % size
	end
	local out = ""
	for _ = 1, width do
		local digit = h % 64
		out = string.sub(SL_HASH_DIGITS, digit + 1, digit + 1) .. out
		h = (h - digit) / 64
	end
	return out
end

local function SL_OrderRealmKey()
	local accountName = CharacterSelect._loggedInAccount
	if not accountName or accountName == "" then
		accountName = GetSavedAccountName() or "default"
	end
	accountName = string.gsub(accountName, "[;=|/]", "")
	if accountName == "" then accountName = "default" end
	local serverName = string.gsub(GetServerName() or "default", "[;=|/]", "")
	if serverName == "" then serverName = "default" end
	return serverName .. "/" .. accountName
end

local function SL_OrderAccountHash()
	return SL_Hash(string.upper(SL_OrderRealmKey()), 3)
end

local function SL_IsCompactEntry(entry)
	return string.len(entry) >= 3 and (string.len(entry) % 3) == 0
		and string.find(entry, "^[%w%-_]+$") ~= nil
end

local function SL_LoadOrderEntries()
	local entries = {}
	for entry in string.gmatch(GetCVar(SL_CHARORDER_CVAR) or "", "[^;]+") do
		table.insert(entries, entry)
	end
	return entries
end

local function SL_CharacterKey(serverIndex)
	local key
	local name, race, class = GetCharacterInfo(serverIndex)
	if name then
		key = string.gsub(name .. "#" .. (race or "") .. "#" .. (class or ""), "[;=|/]", "")
	end
	return key
end

local function SL_CompactOrder(entry, numChars)
	local order, used = {}, {}
	local hashes = {}
	for s = 1, numChars do
		hashes[s] = SL_Hash(SL_CharacterKey(s) or "", 2)
	end
	for i = 4, string.len(entry) - 2, 3 do
		local index = string.find(SL_HASH_DIGITS, string.sub(entry, i, i), 1, true)
		local hash = string.sub(entry, i + 1, i + 2)
		local found
		if index and index <= numChars and not used[index] and hashes[index] == hash then
			found = index
		else
			for s = 1, numChars do
				if not found and not used[s] and hashes[s] == hash then found = s end
			end
		end
		if found then
			table.insert(order, found)
			used[found] = true
		end
	end
	return order, used
end

local function SL_ReadableOrder(list, numChars)
	local order, used = {}, {}
	for entry in string.gmatch(list, "[^|]+") do
		local byName = not string.find(entry, "#", 1, true)
		for s = 1, numChars do
			local key = SL_CharacterKey(s)
			local match = key and (key == entry or (byName and (GetCharacterInfo(s)) == entry))
			if not used[s] and match then
				table.insert(order, s)
				used[s] = true
				break
			end
		end
	end
	return order, used
end

local function SL_RefreshOrder()
	local numChars = GetNumCharacters()
	local accountHash = SL_OrderAccountHash()
	local readableKey = SL_OrderRealmKey() .. "="
	local compact, readable
	for _, entry in ipairs(SL_LoadOrderEntries()) do
		if not compact and SL_IsCompactEntry(entry) and string.sub(entry, 1, 3) == accountHash then
			compact = entry
		elseif not readable and string.sub(entry, 1, string.len(readableKey)) == readableKey then
			readable = string.sub(entry, string.len(readableKey) + 1)
		end
	end
	local order, used
	if compact then
		order, used = SL_CompactOrder(compact, numChars)
	else
		order, used = SL_ReadableOrder(readable or "", numChars)
	end
	for s = 1, numChars do
		if not used[s] then
			table.insert(order, s)
			used[s] = true
		end
	end
	CharacterSelect.displayOrder = order
	CharacterSelect.displayRowOf = {}
	for row, s in ipairs(order) do
		CharacterSelect.displayRowOf[s] = row
	end
end

local function SL_PersistOrder()
	local accountHash = SL_OrderAccountHash()
	local readableKey = SL_OrderRealmKey() .. "="
	local own = accountHash
	for _, s in ipairs(CharacterSelect.displayOrder or {}) do
		local key = SL_CharacterKey(s)
		if key and s <= 64 then
			own = own .. string.sub(SL_HASH_DIGITS, s, s) .. SL_Hash(key, 2)
		end
	end
	local parts = { own }
	local length = string.len(own)
	for _, entry in ipairs(SL_LoadOrderEntries()) do
		local compact = SL_IsCompactEntry(entry)
		local valid = (compact and string.sub(entry, 1, 3) ~= accountHash)
			or (not compact and string.find(entry, "^[^=\"]+/[^=\"]+=[^=\"]+$") ~= nil
				and string.sub(entry, 1, string.len(readableKey)) ~= readableKey)
		if valid and length + 1 + string.len(entry) <= SL_CHARORDER_MAX then
			table.insert(parts, entry)
			length = length + 1 + string.len(entry)
		end
	end
	SetCVar(SL_CHARORDER_CVAR, table.concat(parts, ";"))
end

local SL_reorderGhost

local function SL_UpdateReorderGhost()
	SL_reorderGhost = SL_reorderGhost or SL_FindReorderGhost()
	local g = SL_reorderGhost
	if g and g:IsShown() then
		local row = CharacterSelect._dragRow
		if row and g.alRow ~= row then
			SL_StyleReorderGhost(g, row)
		end
		if g.alHighlight then
			SL_SetSliceAlpha(g.alHighlight, g.hl:GetAlpha())
		end
	elseif g then
		g.alRow = nil
	end
end

SL_char_hooks_done = false

-- The hook frame can fire before or after CharacterSelect has built the
-- list, so it installs the hooks for later updates and styles once now.
function SL_InstallCharSelectHooks()
	if not SL_char_hooks_done then
		if UpdateCharacterList then
			local orig = UpdateCharacterList
			UpdateCharacterList = function(...)
				orig(...)
				SL_SafeStyle()
			end
		end

		-- Synastria may do more here than move the highlight, so the
		-- client's own implementation always runs first.
		if UpdateCharacterSelection then
			local orig = UpdateCharacterSelection
			UpdateCharacterSelection = function(self, ...)
				orig(self, ...)
				SL_SafeStyle()
			end
		end

		if CharacterSelect_RefreshOrder and CharacterSelect_PersistOrder then
			CharacterSelect_RefreshOrder = SL_RefreshOrder
			CharacterSelect_PersistOrder = SL_PersistOrder
			-- The list was likely built before this ran, so rebuild it once
			-- with the new keys.
			if UpdateCharacterList then UpdateCharacterList() end
		end

		-- The reorder script creates its highlight textures on its first
		-- frames, so it keeps running until every row has one.
		if CharacterSelect_UpdateHighlights then
			local orig = CharacterSelect_UpdateHighlights
			CharacterSelect_UpdateHighlights = function(elapsed)
				local ready = true
				for i = 1, (MAX_CHARACTERS_DISPLAYED or 10) do
					local btn = getglobal("CharSelectCharacterButton" .. i)
					if btn and not btn.selection then ready = false end
				end
				if ready and SL_STYLE.enabled then
					SL_ReorderUpdateHighlights(elapsed)
					pcall(SL_UpdateReorderGhost)
				else
					orig(elapsed)
				end
			end
		end

		SL_char_hooks_done = true
	end
end