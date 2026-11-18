--[[
	CharacterSelectReorder.lua
	Reorder characters on the character select screen via drag & drop (WoW 3.3.5a, GlueXML).

	Contains the complete system:
	  1. Saving/loading the order (client-side in a CVar, per realm + account)
	  2. Smooth highlight for the character buttons (hover + selection fade instead of popping)
	  3. Drag "ghost" with up/down arrows as an indicator
	  4. Drag & drop: the other entries make room live, the dragged one settles in smoothly

	Installation: see README.md. This file only defines functions and creates frames at runtime;
	it can be loaded via <Script file="CharacterSelectReorder.lua"/> in CharacterSelect.xml or
	pasted at the end of CharacterSelect.lua.

	Public functions (called from CharacterSelect.lua / CharacterSelect.xml):
	  CharacterSelect_RefreshOrder()                   load the order from the CVar
	  CharacterSelect_PersistOrder()                   write the current order to the CVar
	  CharacterSelect_GetServerIndex(row)              list row -> server index of the character
	  CharacterSelect_GetRow(serverIndex)              server index -> list row
	  CharacterSelect_GetNeighbor(serverIndex, delta)  next/previous character in list order
	  CharacterSelect_UpdateHighlights(elapsed)        every frame from CharacterSelect_OnUpdate
	  CharacterSelect_UpdateSelectionHighlight()       replaces Lock/UnlockHighlight in UpdateCharacterSelection
	  CharacterSelect_ConsumeDragClick()               at the start of CharacterSelectButton_OnClick
	  CharacterSelect_BlocksDoubleClick()              at the start of CharacterSelectButton_OnDoubleClick
	  CharacterSelect_IsDragging()                     block arrow keys while dragging
	  CharacterSelect_CancelDrag()                     in CharacterSelect_OnHide / before EnterWorld
	  CharacterSelectButton_OnMouseDown(self, button)  button template (XML)
	  CharacterSelectButton_OnMouseUp(self, button)    button template (XML)

	Fields on the CharacterSelect frame:
	  CharacterSelect.displayOrder[row] = serverIndex
	  CharacterSelect.displayRowOf[serverIndex] = row
	  CharacterSelect._loggedInAccount  (optional, set at login -- see README.md)
]]

-- ============================================================
-- 1) Saving / loading the order
-- 3.3.5a has no server-side MoveCharacter API, so the order is stored client-side in a CVar.
-- Uses realmListbn (Battle.net realm list, unused on private 3.3.5a servers); it is saved
-- in Config.wtf.
-- Format: "Realm/Account=Name1|Name2|Name3;Realm/Account2=CharA|CharB"
-- Names are stored (not indices) -> new/deleted characters don't break anything:
-- unknown ones go to the end, missing ones are skipped.
-- ============================================================
local CHARORDER_CVAR = "realmListbn";

local function csr_GetRealmKey()
	-- The account captured at login takes precedence (independent of "remember account name").
	-- Falls back to GetSavedAccountName if _loggedInAccount was not set.
	local accountName = CharacterSelect._loggedInAccount;
	if not accountName or accountName == "" then
		accountName = GetSavedAccountName() or "default";
	end
	accountName = string.gsub(accountName, "[;=|/]", "");
	if accountName == "" then accountName = "default"; end

	local serverName = GetServerName() or "default";
	serverName = string.gsub(serverName, "[;=|/]", "");
	if serverName == "" then serverName = "default"; end

	return serverName .. "/" .. accountName;
end

local function csr_LoadAllOrders()
	local cvar = GetCVar(CHARORDER_CVAR) or "";
	local result = {};
	for entry in string.gmatch(cvar, "[^;]+") do
		local key, names = string.match(entry, "^([^=]+)=(.*)$");
		if key then
			result[key] = names;
		end
	end
	return result;
end

local function csr_LoadOrderNames()
	local all = csr_LoadAllOrders();
	local names = all[csr_GetRealmKey()] or "";
	local list = {};
	for s in string.gmatch(names, "[^|]+") do
		table.insert(list, s);
	end
	return list;
end

function CharacterSelect_RefreshOrder()
	local numChars = GetNumCharacters();
	local nameList = csr_LoadOrderNames();
	local order = {};
	local used = {};
	for _, savedName in ipairs(nameList) do
		for s = 1, numChars do
			if not used[s] then
				local name = GetCharacterInfo(s);
				if name == savedName then
					table.insert(order, s);
					used[s] = true;
					break;
				end
			end
		end
	end
	for s = 1, numChars do
		if not used[s] then
			table.insert(order, s);
			used[s] = true;
		end
	end
	CharacterSelect.displayOrder = order;
	CharacterSelect.displayRowOf = {};
	for row, s in ipairs(order) do
		CharacterSelect.displayRowOf[s] = row;
	end
end

function CharacterSelect_PersistOrder()
	local names = {};
	if CharacterSelect.displayOrder then
		for _, s in ipairs(CharacterSelect.displayOrder) do
			local name = GetCharacterInfo(s);
			if name then table.insert(names, name); end
		end
	end
	local all = csr_LoadAllOrders();
	all[csr_GetRealmKey()] = table.concat(names, "|");
	local parts = {};
	for r, n in pairs(all) do
		if n and n ~= "" and string.find(r, "/", 1, true) then
			table.insert(parts, r .. "=" .. n);
		end
	end
	SetCVar(CHARORDER_CVAR, table.concat(parts, ";"));
end

function CharacterSelect_GetServerIndex(row)
	return (CharacterSelect.displayOrder and CharacterSelect.displayOrder[row]) or row;
end

function CharacterSelect_GetRow(serverIndex)
	return (CharacterSelect.displayRowOf and CharacterSelect.displayRowOf[serverIndex]) or serverIndex;
end

-- Next (delta = 1) or previous (delta = -1) character in list order, wrapping around.
function CharacterSelect_GetNeighbor(serverIndex, delta)
	local numChars = GetNumCharacters();
	if ( numChars < 1 ) then return 0; end
	local row = CharacterSelect_GetRow(serverIndex) + delta;
	if ( row < 1 ) then row = numChars; elseif ( row > numChars ) then row = 1; end
	return CharacterSelect_GetServerIndex(row);
end

-- ============================================================
-- 2) Animated highlight for the character buttons (hover + selection).
-- The engine HighlightTexture (hover) and LockHighlight (selection) pop in instantly. Instead,
-- a dedicated highlight texture (button.selection, same artwork, ADD) is faded via OnUpdate:
-- target alpha 1 if the button is hovered OR selected, otherwise 0.
-- Drag & drop relies on this highlight (the ghost mirrors it) -- hence part of the system.
-- ============================================================
local CSR_HL_TEXTURE = "Interface\\Glues\\CharacterSelect\\Glue-CharacterSelect-Highlight";
local CSR_HL_FADE_SPEED = 6;   -- alpha per second (higher = faster)
local CSR_HL_TEX_TOP = 8;      -- highlight texture top edge above the button top
local CSR_HL_TEX_BOTTOM = 66;  -- highlight texture bottom edge below the button top (texture is 74 high)

local function csr_SetupHighlight(button)
	if ( button._hlSetup ) then return; end
	button._hlSetup = true;
	button:SetHighlightTexture("");   -- disable the engine hover/lock pop
	local sel = button.selection;
	if ( not sel ) then
		-- template without a selection texture: create it
		sel = button:CreateTexture(nil, "OVERLAY");
		sel:SetTexture(CSR_HL_TEXTURE);
		sel:SetBlendMode("ADD");
		button.selection = sel;
	end
	sel:SetDrawLayer("OVERLAY");
	sel:SetSize(256, 74);
	sel:ClearAllPoints();
	sel:SetPoint("TOPLEFT", -20, 8);
	sel:SetAlpha(0);
	sel:Show();
end

local function csr_CursorXY()
	local first = _G["CharSelectCharacterButton1"];
	if ( not first or not first:GetLeft() ) then return nil; end
	local s = first:GetEffectiveScale();
	local mx, my = GetCursorPosition();
	return mx / s, my / s;
end

-- Index of the hovered character button (or nil). Candidate = cursor inside its highlight
-- texture; on overlap the nearest texture center wins. The selected character takes precedence.
local function csr_HoveredIndex()
	local mx, my = csr_CursorXY();
	if ( not mx ) then return nil; end
	local center = (CSR_HL_TEX_TOP - CSR_HL_TEX_BOTTOM) / 2;
	local best, bestIdx;
	for i = 1, MAX_CHARACTERS_DISPLAYED do
		local b = _G["CharSelectCharacterButton"..i];
		if ( b and b:IsShown() and b:GetLeft()
			and mx >= b:GetLeft() and mx <= b:GetRight()
			and my <= b:GetTop() + CSR_HL_TEX_TOP and my >= b:GetTop() - CSR_HL_TEX_BOTTOM ) then
			if ( b._selected ) then return i; end
			local dd = math.abs(my - (b:GetTop() + center));
			if ( not best or dd < best ) then best, bestIdx = dd, i; end
		end
	end
	return bestIdx;
end

function CharacterSelect_UpdateHighlights(elapsed)
	local hov = csr_HoveredIndex();
	-- While dragging, the dragged entry counts as hovered (its ghost mirrors this highlight);
	-- rows sliding past under the cursor don't light up.
	if ( CharacterSelect._dragRow ) then hov = CharacterSelect._dragRow; end
	for i = 1, MAX_CHARACTERS_DISPLAYED do
		local button = _G["CharSelectCharacterButton"..i];
		if ( button ) then
			csr_SetupHighlight(button);
			local sel = button.selection;
			local target = ( button:IsShown() and (button._selected or i == hov) ) and 1 or 0;
			local cur = sel:GetAlpha();
			if ( cur ~= target ) then
				local step = CSR_HL_FADE_SPEED * elapsed;
				if ( cur < target ) then
					cur = math.min(target, cur + step);
				else
					cur = math.max(target, cur - step);
				end
				sel:SetAlpha(cur);
			end
		end
	end
end

-- Replaces Lock/UnlockHighlight in UpdateCharacterSelection: marks the row of the selected
-- character (list order!); UpdateHighlights does the fading.
function CharacterSelect_UpdateSelectionHighlight()
	local row = CharacterSelect.displayRowOf and CharacterSelect.displayRowOf[CharacterSelect.selectedIndex];
	for i = 1, MAX_CHARACTERS_DISPLAYED do
		local button = _G["CharSelectCharacterButton"..i];
		csr_SetupHighlight(button);
		button:UnlockHighlight();
		button._selected = ( row and row > 0 and row <= MAX_CHARACTERS_DISPLAYED and i == row ) and true or false;
	end
end

-- ============================================================
-- 3) Ghost for drag & drop: freely positionable copy of a list entry (name/info/location,
-- faction icon if present, highlight). Font and position of the texts are copied from the
-- real button at runtime, so the ghost adapts to the button layout. Plus the up/down
-- arrows -- purely an indicator on the grabbed entry that it can be moved (no function).
-- When the entry is at the very top or bottom, the arrow shows the disabled artwork.
-- ============================================================
local CSR_ARROW_TEX = "Interface\\Buttons\\UI-ScrollBar-ScrollUpButton-Up";
local CSR_ARROW_TEX_DISABLED = "Interface\\Buttons\\UI-ScrollBar-ScrollUpButton-Disabled";
local CSR_ARROW_X = -60;        -- arrows: offset from the right button edge
local CSR_ARROW_UP_Y = -3;      -- up arrow: offset from the button top
local CSR_ARROW_DOWN_Y = -29;   -- down arrow: offset from the button top

local csr_rowSpacing = 57;      -- vertical spacing of the list entries (measured on the first drag)
local csr_dragGhost;

-- Arrow texture on the ghost; down = vertically flipped up artwork.
local function csr_CreateGhostArrow(parent, file, y, flip)
	local t = parent:CreateTexture(nil, "ARTWORK");
	t:SetTexture(file);
	t:SetSize(24, 24);
	t:SetPoint("TOPRIGHT", parent, "TOPRIGHT", CSR_ARROW_X, y);
	if ( flip ) then t:SetTexCoord(0, 1, 1, 0); end
	t:SetAlpha(0);
	return t;
end

local function csr_GetDragGhost()
	if ( csr_dragGhost ) then return csr_dragGhost; end
	local b1 = CharSelectCharacterButton1;
	local g = CreateFrame("Frame", nil, b1:GetParent());
	g:SetSize(b1:GetWidth(), b1:GetHeight());
	g:SetFrameStrata("HIGH");
	g.hl = g:CreateTexture(nil, "BACKGROUND");
	g.hl:SetTexture(CSR_HL_TEXTURE);
	g.hl:SetBlendMode("ADD");
	g.hl:SetSize(256, 74);
	g.hl:SetPoint("TOPLEFT", -20, 8);
	g.name = g:CreateFontString(nil, "OVERLAY", "GlueFontNormal");
	g.info = g:CreateFontString(nil, "OVERLAY", "GlueFontHighlightSmall");
	g.loc  = g:CreateFontString(nil, "OVERLAY", "GlueFontDisableSmall");
	g.faction = g:CreateTexture(nil, "ARTWORK");
	-- arrows in their own child frame so they are guaranteed to be drawn above text and icon
	local arrows = CreateFrame("Frame", nil, g);
	arrows:SetAllPoints(g);
	arrows:SetFrameLevel(g:GetFrameLevel() + 5);
	g.upOn    = csr_CreateGhostArrow(arrows, CSR_ARROW_TEX, CSR_ARROW_UP_Y);
	g.upOff   = csr_CreateGhostArrow(arrows, CSR_ARROW_TEX_DISABLED, CSR_ARROW_UP_Y);
	g.downOn  = csr_CreateGhostArrow(arrows, CSR_ARROW_TEX, CSR_ARROW_DOWN_Y, true);
	g.downOff = csr_CreateGhostArrow(arrows, CSR_ARROW_TEX_DISABLED, CSR_ARROW_DOWN_Y, true);
	g:Hide();
	csr_dragGhost = g;
	return g;
end

-- Arrow indicator on the ghost: alpha = overall visibility, upLim/downLim = 0..1 crossfade
-- to the disabled artwork (entry is at the very top or bottom).
local function csr_SetGhostArrows(alpha, upLim, downLim)
	local g = csr_GetDragGhost();
	g.upOn:SetAlpha(alpha * (1 - upLim));
	g.upOff:SetAlpha(alpha * upLim);
	g.downOn:SetAlpha(alpha * (1 - downLim));
	g.downOff:SetAlpha(alpha * downLim);
end

-- Copy a button FontString onto the ghost: text, font, color, width, position.
local function csr_CopyFontString(dst, src, btn)
	if ( not src ) then dst:Hide(); return; end
	local fo = src.GetFontObject and src:GetFontObject();
	if ( fo ) then
		dst:SetFontObject(fo);
	else
		local font, size, flags = src:GetFont();
		if ( font ) then dst:SetFont(font, size, flags); end
	end
	dst:SetTextColor(src:GetTextColor());
	dst:SetJustifyH(src:GetJustifyH());
	dst:SetWidth(src:GetWidth() + 1);
	dst:SetHeight(src:GetHeight());
	dst:ClearAllPoints();
	local l, t, bl, bt = src:GetLeft(), src:GetTop(), btn:GetLeft(), btn:GetTop();
	if ( l and t and bl and bt ) then
		dst:SetPoint("TOPLEFT", dst:GetParent(), "TOPLEFT", l - bl, t - bt);
	else
		dst:SetPoint("TOPLEFT", dst:GetParent(), "TOPLEFT", 0, 0);
	end
	dst:SetText(src:GetText() or "");
	dst:Show();
end

local function csr_FillGhost(g, idx)
	local btn = _G["CharSelectCharacterButton"..idx];
	csr_CopyFontString(g.name, _G["CharSelectCharacterButton"..idx.."ButtonTextName"], btn);
	csr_CopyFontString(g.info, _G["CharSelectCharacterButton"..idx.."ButtonTextInfo"], btn);
	csr_CopyFontString(g.loc,  _G["CharSelectCharacterButton"..idx.."ButtonTextLocation"], btn);
	local fe = btn and btn.FactionEmblem;   -- optional (faction icon added by some client mods)
	if ( fe and fe:IsShown() ) then
		g.faction:SetTexture(fe:GetTexture());
		g.faction:SetSize(fe:GetWidth(), fe:GetHeight());
		g.faction:SetTexCoord(fe:GetTexCoord());
		local pt, _, rp, x, y = fe:GetPoint(1);
		g.faction:ClearAllPoints();
		g.faction:SetPoint(pt or "TOPRIGHT", g, rp or "TOPRIGHT", x or -37, y or -7);
		g.faction:Show();
	else
		g.faction:Hide();
	end
end

-- ============================================================
-- 4) Drag & drop: hold the left mouse button on an entry and drag vertically. The ghost
-- follows the cursor and fades in the arrows, the original entry becomes a gap, and the
-- entries between start and target position slide one row aside live. On release the entry
-- is MOVED to the target row (not swapped), the arrows fade out and the ghost slides into
-- the gap. As long as the cursor stays below CSR_DRAG_THRESHOLD, it's a normal click.
--
-- So that individual rows can move, the XML anchor chain (button N anchored to button N-1)
-- is replaced once, on the first drag, by fixed anchors on an invisible reference frame
-- (= position of button 1) -- pixel-identical layout, but each row has its own y offset.
-- The service buttons (customize/race change/faction change) are anchored to their
-- character button and move along.
-- ============================================================
local CSR_DRAG_THRESHOLD = 6;           -- cursor travel in px after which a click becomes a drag
local CSR_DRAG_SHIFT_SPEED = 16;        -- speed at which the other rows make room (higher = faster)
local CSR_DRAG_SETTLE_DURATION = 0.12;  -- ghost slides into the gap after release (seconds)
local CSR_DRAG_ARROW_FADE = 0.15;       -- arrow indicator fade in/out (seconds)
local CSR_ROW_SERVICE_BUTTONS = { "CharSelectCharacterCustomize", "CharSelectRaceChange", "CharSelectFactionChange" };

local csr_dragFrame;
local csr_rowAnchor;          -- fixed reference point of the list (original position of button 1)
local csr_rowOff = {};        -- current y offset per row (>0 = up)
local csr_rowOffTarget = {};  -- target offset per row

local function csr_RowY(row)
	return -(row - 1) * csr_rowSpacing;
end

local function csr_ApplyRowOffset(row)
	_G["CharSelectCharacterButton"..row]:SetPoint("TOPLEFT", csr_rowAnchor, "TOPLEFT", 0, csr_RowY(row) + csr_rowOff[row]);
end

-- Break up the anchor chain once (see above). Measure the row spacing from the layout first.
local function csr_DetachRows()
	if ( csr_rowAnchor ) then return; end
	local b1, b2 = CharSelectCharacterButton1, CharSelectCharacterButton2;
	local t1, t2 = b1:GetTop(), b2 and b2:GetTop();
	if ( t1 and t2 and t1 - t2 > 1 ) then
		csr_rowSpacing = t1 - t2;
	end
	local p, rel, rp, x, y = b1:GetPoint(1);
	csr_rowAnchor = CreateFrame("Frame", nil, b1:GetParent());
	csr_rowAnchor:SetSize(b1:GetWidth(), b1:GetHeight());   -- same size -> same TOPLEFT corner as button 1
	csr_rowAnchor:SetPoint(p, rel or b1:GetParent(), rp, x, y);
	for i = 1, MAX_CHARACTERS_DISPLAYED do
		csr_rowOff[i] = 0;
		csr_rowOffTarget[i] = 0;
		_G["CharSelectCharacterButton"..i]:ClearAllPoints();
		csr_ApplyRowOffset(i);
	end
end

-- Ease the row offsets towards their targets (smooth deceleration, frame-rate independent).
-- Returns true while any row is still moving.
local function csr_StepRowOffsets(dt)
	local k = 1 - math.exp(-CSR_DRAG_SHIFT_SPEED * dt);
	local moving = false;
	for i = 1, MAX_CHARACTERS_DISPLAYED do
		local cur, tgt = csr_rowOff[i], csr_rowOffTarget[i];
		if ( cur ~= tgt ) then
			cur = cur + (tgt - cur) * k;
			if ( math.abs(tgt - cur) < 0.5 ) then cur = tgt; else moving = true; end
			csr_rowOff[i] = cur;
			csr_ApplyRowOffset(i);
		end
	end
	return moving;
end

-- Move a value towards the target by at most step.
local function csr_Approach(cur, tgt, step)
	if ( cur < tgt ) then return math.min(tgt, cur + step); end
	return math.max(tgt, cur - step);
end

-- Show/hide a row's character button together with its service buttons.
local function csr_SetRowAlpha(row, alpha)
	_G["CharSelectCharacterButton"..row]:SetAlpha(alpha);
	for _, prefix in ipairs(CSR_ROW_SERVICE_BUTTONS) do
		local b = _G[prefix..row];
		if ( b ) then b:SetAlpha(alpha); end
	end
end

local function csr_SetGhostDy(dy)
	csr_GetDragGhost():SetPoint("TOPLEFT", csr_rowAnchor, "TOPLEFT", 0, dy);
end

-- The ghost's highlight mirrors the (animated) highlight of the row it represents: the source
-- row while dragging (counts as hovered), the target row while settling. That way there is no
-- brightness jump when picking up or when swapping ghost -> real button.
local function csr_SyncGhostHighlight(row)
	local b = row and _G["CharSelectCharacterButton"..row];
	local sel = b and b.selection;
	if ( sel ) then csr_GetDragGhost().hl:SetAlpha(sel:GetAlpha()); end
end

-- Ghost top edge relative to the start of the list (<= 0), clamped to the occupied rows.
local function csr_DragGhostDy(d)
	local _, my = csr_CursorXY();
	local top = csr_rowAnchor:GetTop();
	if ( not my or not top ) then return d.dy or 0; end
	local dy = my + d.grabOffset - top;
	local minDy = csr_RowY(d.numRows);
	if ( dy > 0 ) then dy = 0; elseif ( dy < minDy ) then dy = minDy; end
	return dy;
end

local function csr_DragReset()
	local d = csr_dragFrame;
	if ( not d ) then return; end
	d:SetScript("OnUpdate", nil); d:Hide();
	if ( d.srcRow ) then csr_SetRowAlpha(d.srcRow, 1); end
	if ( d.destRow ) then csr_SetRowAlpha(d.destRow, 1); end
	d.pressed = false; d.active = false; d.srcRow = nil; d.destRow = nil;
	if ( csr_rowAnchor ) then
		for i = 1, MAX_CHARACTERS_DISPLAYED do
			if ( csr_rowOff[i] ~= 0 or csr_rowOffTarget[i] ~= 0 ) then
				csr_rowOff[i] = 0; csr_rowOffTarget[i] = 0;
				csr_ApplyRowOffset(i);
			end
		end
	end
	if ( csr_dragGhost ) then csr_dragGhost:Hide(); end
	CharacterSelect._dragRow = nil;
	CharacterSelect._dragging = false;
end

local function csr_DragBegin(d)
	csr_DetachRows();
	d.active = true;
	CharacterSelect._dragging = true;      -- blocks new drags (also while settling) and arrow keys
	CharacterSelect._dragRow = d.srcRow;   -- for CharacterSelect_UpdateHighlights: counts as hovered
	-- optional: cancel a running auto-login countdown (if the character select screen has one)
	if ( CharacterSelect._autoTimerRunning and CharacterSelect_StopAutoTimer ) then
		CharacterSelect_StopAutoTimer(false);
	end
	local g = csr_GetDragGhost();
	csr_FillGhost(g, d.srcRow);
	d.dy = csr_RowY(d.srcRow);
	d.target = d.srcRow;
	-- arrows fade in from 0; the disabled state of the start row applies immediately
	d.arrowAlpha = 0;
	d.upLim = (d.srcRow <= 1) and 1 or 0;
	d.downLim = (d.srcRow >= d.numRows) and 1 or 0;
	csr_SetGhostArrows(0, d.upLim, d.downLim);
	g:ClearAllPoints();
	csr_SetGhostDy(d.dy);
	csr_SyncGhostHighlight(d.srcRow);
	g:Show();
	csr_SetRowAlpha(d.srcRow, 0);   -- the ghost represents the entry, its row becomes the gap
end

local function csr_DragSettleOnUpdate(self, dt)
	local moving = csr_StepRowOffsets(dt);
	if ( not self.settled ) then
		self.elapsed = self.elapsed + dt;
		local t = self.elapsed / CSR_DRAG_SETTLE_DURATION;
		if ( t > 1 ) then t = 1; end
		local e = t * t * (3 - 2 * t);   -- smoothstep
		csr_SetGhostDy(self.fromDy + (self.toDy - self.fromDy) * e);
		csr_SyncGhostHighlight(self.destRow);
		self.arrowAlpha = math.max(0, self.arrowAlpha - dt / CSR_DRAG_ARROW_FADE);
		csr_SetGhostArrows(self.arrowAlpha, self.upLim, self.downLim);
		if ( t >= 1 and self.arrowAlpha <= 0 ) then
			-- ghost sits exactly in the slot and the arrows are gone: swap it for the real button
			self.settled = true;
			csr_GetDragGhost():Hide();
			csr_SetRowAlpha(self.destRow, 1);
		end
	end
	if ( self.settled and not moving ) then csr_DragReset(); end
end

-- Release: apply=true moves the entry to d.target, otherwise everything slides back.
local function csr_DragFinish(apply)
	local d = csr_dragFrame;
	if ( not d ) then return; end
	if ( not d.active ) then csr_DragReset(); return; end
	d.active = false;
	d.pressed = false;
	CharacterSelect._dragRow = nil;
	CharacterSelect._dragSuppressClick = true;   -- the click that ends the drag must not select
	local src = d.srcRow;
	local target = apply and d.target or src;
	local order = CharacterSelect.displayOrder;
	local destRow = src;
	if ( target ~= src and order and order[src] ) then
		-- Remember each character's visible state (height incl. make-room offset, highlight alpha):
		-- after the buttons are refilled every character sits exactly where it was -> no jumps/pops.
		local dragged = order[src];
		local oldRowOf, oldY, oldSel = {}, {}, {};
		for i = 1, d.numRows do
			if ( order[i] ) then
				oldRowOf[order[i]] = i;
				oldY[i] = csr_RowY(i) + csr_rowOff[i];
				local sel = _G["CharSelectCharacterButton"..i].selection;
				oldSel[i] = sel and sel:GetAlpha() or 0;
			end
		end
		table.insert(order, target, table.remove(order, src));
		CharacterSelect_PersistOrder();
		csr_SetRowAlpha(src, 1);
		UpdateCharacterList();                       -- refills the buttons (order read from the CVar)
		UpdateCharacterSelection(CharacterSelect);   -- move the selection marker to the new rows right away
		destRow = (CharacterSelect.displayRowOf and CharacterSelect.displayRowOf[dragged]) or src;
		local newOrder = CharacterSelect.displayOrder;
		for k = 1, d.numRows do
			local i = newOrder[k] and oldRowOf[newOrder[k]];
			if ( i ) then
				csr_rowOff[k] = (k == destRow) and 0 or (oldY[i] - csr_RowY(k));
				csr_ApplyRowOffset(k);
				local sel = _G["CharSelectCharacterButton"..k].selection;
				if ( sel ) then sel:SetAlpha(oldSel[i]); end
			end
		end
	end
	for i = 1, MAX_CHARACTERS_DISPLAYED do csr_rowOffTarget[i] = 0; end
	d.destRow = destRow;
	csr_SetRowAlpha(destRow, 0);   -- invisible until the ghost has settled
	d.fromDy = d.dy;
	d.toDy = csr_RowY(destRow);
	d.elapsed = 0;
	d.settled = false;
	d:SetScript("OnUpdate", csr_DragSettleOnUpdate);
end

local function csr_DragOnUpdate(self, dt)
	if ( CharacterSelect._enteringWorld ) then csr_DragReset(); return; end
	-- fallback in case OnMouseUp doesn't reach the button
	local released = IsMouseButtonDown and not IsMouseButtonDown("LeftButton");
	if ( not self.active ) then
		if ( released ) then csr_DragReset(); return; end
		local _, my = csr_CursorXY();
		if ( not my or math.abs(my - self.startY) < CSR_DRAG_THRESHOLD ) then return; end
		csr_DragBegin(self);
	elseif ( released ) then
		csr_DragFinish(true);
		return;
	end
	local dy = csr_DragGhostDy(self);
	self.dy = dy;
	csr_SetGhostDy(dy);
	csr_SyncGhostHighlight(self.srcRow);
	local target = math.floor(-dy / csr_rowSpacing + 0.5) + 1;
	if ( target < 1 ) then target = 1; elseif ( target > self.numRows ) then target = self.numRows; end
	self.target = target;
	-- fade in the arrow indicator; crossfade to the disabled artwork at the very top/bottom
	local step = dt / CSR_DRAG_ARROW_FADE;
	self.arrowAlpha = math.min(1, self.arrowAlpha + step);
	self.upLim = csr_Approach(self.upLim, (target <= 1) and 1 or 0, step);
	self.downLim = csr_Approach(self.downLim, (target >= self.numRows) and 1 or 0, step);
	csr_SetGhostArrows(self.arrowAlpha, self.upLim, self.downLim);
	-- rows between start and target move one row towards the gap
	local src = self.srcRow;
	for i = 1, self.numRows do
		if ( i > src and i <= target ) then
			csr_rowOffTarget[i] = csr_rowSpacing;    -- dragging down: rows below slide up
		elseif ( i < src and i >= target ) then
			csr_rowOffTarget[i] = -csr_rowSpacing;   -- dragging up: rows above slide down
		else
			csr_rowOffTarget[i] = 0;
		end
	end
	csr_StepRowOffsets(dt);
end

function CharacterSelect_CancelDrag()
	csr_DragReset();
end

function CharacterSelect_IsDragging()
	return CharacterSelect._dragging and true or false;
end

-- For CharacterSelectButton_OnClick: true = ignore this click (it ends a drag).
function CharacterSelect_ConsumeDragClick()
	if ( CharacterSelect._dragRow or CharacterSelect._dragSuppressClick ) then
		CharacterSelect._dragSuppressClick = false;
		return true;
	end
	return false;
end

-- For CharacterSelectButton_OnDoubleClick: true = ignore the double click.
function CharacterSelect_BlocksDoubleClick()
	return ( CharacterSelect._dragRow or CharacterSelect._dragSuppressClick ) and true or false;
end

function CharacterSelectButton_OnMouseDown(self, button)
	CharacterSelect._dragSuppressClick = false;
	if ( button ~= "LeftButton" ) then return; end
	if ( CharacterSelect._dragging or CharacterSelect._enteringWorld ) then return; end
	if ( not CharacterSelect.displayRowOf ) then return; end
	local numRows = math.min(GetNumCharacters(), MAX_CHARACTERS_DISPLAYED);
	if ( numRows < 2 ) then return; end
	local row = CharacterSelect.displayRowOf[self:GetID()];
	local _, my = csr_CursorXY();
	if ( not row or row > numRows or not my or not self:GetTop() ) then return; end
	if ( not csr_dragFrame ) then csr_dragFrame = CreateFrame("Frame"); csr_dragFrame:Hide(); end
	local d = csr_dragFrame;
	d.pressed = true;
	d.active = false;
	d.srcRow = row;
	d.destRow = nil;
	d.numRows = numRows;
	d.startY = my;
	d.grabOffset = self:GetTop() - my;
	d:SetScript("OnUpdate", csr_DragOnUpdate);
	d:Show();
end

function CharacterSelectButton_OnMouseUp(self, button)
	if ( button ~= "LeftButton" ) then return; end
	if ( csr_dragFrame and csr_dragFrame.pressed ) then
		csr_DragFinish(true);
	end
end
