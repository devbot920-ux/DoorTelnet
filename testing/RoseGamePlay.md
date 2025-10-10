# DO NOT UPDATE THIS FILE DIRECTLY 
# This file is auto-generated from the RoseGamePlay.txt file in the game directory.
# The Rose - Council of Guardians (Game Summary)

## Overview
A fantasy text-based role-playing game set in the world of Corinthia ("The Rose"). Players connect via Telnet, create characters, explore the world, and engage in combat, quests, and communication.

---
## Character Creation
- **Races:** Human, Elf, Dwarf, Gnome, Giant, Fairfolk — each with unique stats and traits.
- **Classes (Walks of Life):** Warrior, Scholar, Mage, Priest, Gypsy, Archtypical (jack of all trades).
- **Stats:** Strength, Dexterity, Constitution, Wisdom, Intelligence, Perception, Charisma, Comeliness.
- **Start Tip:** Dwarven Warrior recommended for beginners (high HP). Use `reroll` for best stats.

---
## Gameplay Basics
- **Movement:** N, S, E, W, NW, NE, SW, SE, U, D.
- **Attack/ActionTimer:** Shows cooldowns for actions. shows as /AT=#/AC=# in stats line. Can move rooms when there is an attack timer, but no movement on action timer.
- **NPCs/Monsters:** inside of Cities there are NPC's that are not enimies. Outside of cities there are monsters that will attack you on sight. If attacked and you can fight back fight back with a spell or simply attack.
- **Rest:** `rest` to recover HP/MP.
- **Inventory:** `inv` or `i` to view items.
- **Equipment:** `wear [item]`, `remove [item]`, `arm [weapon]`, `disarm [weapon]`.
- **Look/Scan:** `look [target|direction]`, `scan [direction]`.
- **Encumbrance:** Strength affects carry weight; overburdening costs movement points.
- **Light:** Use `light [torch]`, `snuff [torch]` in dark rooms.
- **Exit Game:** `exit` or `x` saves and quits.

---
## Combat System
- **Attack:** `attack [target]` or `kill [target]` or `a [target]` for autocombat.
- **Weapons:** Must `arm [weapon]`; hand-to-hand uses Empty Hand Combat.
- **Delays:** Attack and action delays determine cooldowns between actions.
- **Health:** HP recovers via Constitution/Natural Healing; mana regenerates over time.
- **Ranged Combat:** `shoot [direction] [target]`, requires ammo.

---
## Magic System
- **Mana:** Energy for casting spells; scales with Intelligence and Constitution.
- **Learn Spells:** Use `learn [spell]` at spell centers; max 50 spells.
- **Cast Spell:** `cast [nickname] [target]`.
- **Healing:** `request [spell] [npc]` (e.g., `request minheal priestess`).
- **Spheres:** Life (healing) and Force (offense).

---
## Progression
- **Experience:** Gained by combat and completing quests.
- **Promotion:** `promote` in guilds; requires XP and gold.
- **Enhance Stats:** `attinfo` + `enhance` at enhancement centers.
- **Proficiencies:** Skills improve with `train` using dev points + gold.

---
## Economy
- **Currency:** Silver (base), Gold, Electrum, Platinum (some unimplemented).
- **Banking:** `deposit`, `withdraw`, `checkbook` for account management.
- **Shops:** `buy`, `sell`, `appraise`, `list`.
- **Charisma:** Affects prices and training cost.

---
## Quests & Advancement
- **Quest Corridors:** Explore to complete tasks for quest points.
- **Requirement:** Quest points + XP needed for level advancement.
- **Maps:** Use `map1`, `map2`, `map3` for guidance.

---
## Social Interaction
- **Global Gossip:** `,on` / `,off` to toggle; `,[message]` to chat globally.
- **Astral Channels:** `-[message]`, tune 1–30000.
- **Whisper:** `/[player] [message]` (private chat).
- **Direct:** `>[player] [message]` (in-room).
- **Groups:** `invite`, `join`, `leave`, `disband`, `gr [message]`.
- **Hordes:** Tame NPCs with `tame [monster]`; command with `horde aggress|defend|disband`.

---
## Survival Elements
- **Hunger/Thirst:** `order food/drink` in taverns; affects HP/MP regen.
- **Poison:** Visit healer or temple (`greet priestess`, `request cure`).
- **Criminal Status:** Wait out or `pay fine` at sheriff’s office.
- **Death:** HP ≤ 0 ends life; may require resurrection or restart (`suicide me now please`).

---
## Tips for New Players
- Use `hint` in key rooms for contextual tips.
- Keep torches, food, and healing potions ready.
- Avoid `kill` (autocombat) unless sure of victory.
- Monitor encumbrance and regeneration.
- Explore and read help with `help [topic]` or `help alpha1`.

---
## Example Commands
```
attack goblin
a g
cast shock orc
learn heal
promote
train melee
deposit 50 gold
group
horde defend
hint
exit
```

---
## Summary Keywords (for model reference)
races, classes, attributes, stats, skills, spells, mana, quests, XP, banking, gossip, astral, whisper, horde, combat, melee, ranged, armor, weapons, tavern, light, map, hint, promote, enhance, train, encumbrance, hunger, thirst.
