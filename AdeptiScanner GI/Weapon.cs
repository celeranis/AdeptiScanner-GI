﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace AdeptiScanner_GI
{
    public class Weapon
    {
        public Tuple<string, int> name;
        public Tuple<string, int, int> level;
        public int? refinement;
        public Tuple<string, string> character;
        public bool locked = false;

        public Weapon()
        {

        }

        public override string ToString()
        {
            string text = "";

            text += "Name: ";
            if (name != null)
                text += name + Environment.NewLine;
            else
                text += "Null-------" + Environment.NewLine;

            text += "Level: ";
            if (level != null)
                text += level + Environment.NewLine;
            else
                text += "Null-------" + Environment.NewLine;

            text += "Refinement: ";
            if (refinement != null)
                text += refinement + Environment.NewLine;
            else
                text += "Null-------" + Environment.NewLine;

            text += "Char: ";
            if (character != null)
                text += character + Environment.NewLine;
            else
                text += "Null" + Environment.NewLine;

            text += "Locked: " + locked + Environment.NewLine;

            return text;
        }

        public JObject toGOODWeapon(bool includeLocation = true)
        {
            JObject result = new JObject();

            if (name != null)
                result.Add("key", JToken.FromObject(name.Item1));
            if (level != null)
            {
                result.Add("level", JToken.FromObject(level.Item2));
                result.Add("ascension", JToken.FromObject(level.Item3));
            }
            if (refinement != null)
                result.Add("refinement", JToken.FromObject(refinement.Value));
            else if (name != null && name.Item2 < 3)
                result.Add("refinement", 1);
            if (includeLocation)
            {
                if (character != null)
                    result.Add("location", JToken.FromObject(character.Item2));
                else
                    result.Add("location", JToken.FromObject(""));
            }

            result.Add("lock", JToken.FromObject(locked));
            return result;
        }

        public static JObject listToGOODWeapons(List<Weapon> items, bool exportEquipStatus)
        {
            JObject result = new JObject();
            JArray weaponJArr = new JArray();
            foreach (Weapon item in items)
            {
                weaponJArr.Add(item.toGOODWeapon(exportEquipStatus));
            }
            result.Add("weapons", weaponJArr);
            return result;
        }
    }
}
