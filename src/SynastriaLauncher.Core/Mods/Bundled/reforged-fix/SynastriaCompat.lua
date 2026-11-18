-- Synastria's client calls CharacterClass_OnClick with the stock 3.3.5 signature (id) and the stock
-- CharacterCreateClassButton/CharacterCreateClassName names. Project Reforged uses (self, id) and
-- CharCreateClassButton, so bridge both.

local reforgedClassOnClick = CharacterClass_OnClick

function CharacterClass_OnClick(id)
	reforgedClassOnClick(_G["CharCreateClassButton" .. id], id)
end

for i = 1, MAX_CLASSES_PER_RACE do
	local button = _G["CharCreateClassButton" .. i]
	button:SetScript("OnClick", function(self)
		CharacterClass_OnClick(self:GetID())
	end)
	_G["CharacterCreateClassButton" .. i] = button
end

CharacterCreateClassName = CharCreateClassInfoFrame.title
