// Sanatate PC - Info PC Electric
// Modelul de date afisat in interfata: sectiuni (componente) -> carduri -> proprietati + constatari.
using System;
using System.Collections.Generic;

namespace InfoPcDisk
{
    public class KV
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public string ValueColor { get; set; }
    }

    public static class Lvl
    {
        // -1 informativ, 0 bun, 1 atentie, 2 problema
        public static string Text(int l) { return l == 0 ? "Bun" : l == 1 ? "Atenție" : l == 2 ? "Problemă" : "Info"; }
        public static string Fg(int l) { return l == 0 ? "#4ADE80" : l == 1 ? "#FBBF24" : l == 2 ? "#F87171" : "#94A3B8"; }
        public static string Bg(int l) { return l == 0 ? "#0B2A1F" : l == 1 ? "#2A1E06" : l == 2 ? "#2A0E0E" : "#1E293B"; }
    }

    public class Card
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Badge { get; set; }
        public string BadgeVisibility { get { return string.IsNullOrEmpty(Badge) ? "Collapsed" : "Visible"; } }
        public string Status { get; set; }
        public string StatusColor { get; set; }
        public string StatusBg { get; set; }
        public List<KV> Props { get; set; }
        public List<string> NoteList = new List<string>();
        public string Notes { get { return NoteList.Count == 0 ? "" : "• " + string.Join("\n• ", NoteList.ToArray()); } }
        public string NotesVisibility { get { return NoteList.Count == 0 || Locked ? "Collapsed" : "Visible"; } }
        public bool Locked { get; set; }
        public string LockVisibility { get { return Locked ? "Visible" : "Collapsed"; } }
        public string PropsVisibility { get { return Locked || Props.Count == 0 ? "Collapsed" : "Visible"; } }
        public string Icon { get; set; }
        public string Target { get; set; }
        public string Hand { get { return string.IsNullOrEmpty(Target) && !Locked ? "Arrow" : "Hand"; } }
        public int Level = -1;

        public Card(string title, string subtitle)
        {
            Title = title;
            Subtitle = subtitle ?? "";
            Props = new List<KV>();
            SetLevel(-1, null);
        }

        public void Add(string key, string value)
        {
            Add(key, value, null);
        }

        public void Add(string key, string value, string color)
        {
            KV kv = new KV();
            kv.Key = key;
            kv.Value = string.IsNullOrEmpty(value) ? "—" : value.Trim();
            kv.ValueColor = color ?? "#E6EAF2";
            Props.Add(kv);
        }

        public void Note(string text)
        {
            if (!string.IsNullOrEmpty(text)) NoteList.Add(text);
        }

        // ridica nivelul doar daca e mai grav decat cel existent
        public void Raise(int level, string note)
        {
            Note(note);
            if (level > Level) SetLevel(level, null);
        }

        public void SetLevel(int level, string status)
        {
            Level = level;
            Status = status ?? Lvl.Text(level);
            StatusColor = Lvl.Fg(level);
            StatusBg = Lvl.Bg(level);
        }
    }

    public class Section
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }
        public string StatusColor { get; set; }
        public string StatusBg { get; set; }
        public string Description { get; set; }
        public string ActionText { get; set; }
        public string ActionVisibility { get { return string.IsNullOrEmpty(ActionText) ? "Collapsed" : "Visible"; } }
        public List<Card> Cards { get; set; }
        public int Level = -1;

        public Section(string key, string name, string icon)
        {
            Key = key;
            Name = name;
            Icon = icon;
            Summary = "";
            Description = "";
            Cards = new List<Card>();
            Finish();
        }

        public Card AddCard(string title, string subtitle)
        {
            Card c = new Card(title, subtitle);
            Cards.Add(c);
            return c;
        }

        public void Finish()
        {
            int l = -1;
            foreach (Card c in Cards) if (c.Level > l) l = c.Level;
            SetLevel(l);
        }

        public void SetLevel(int l)
        {
            Level = l;
            Status = Lvl.Text(l);
            StatusColor = Lvl.Fg(l);
            StatusBg = Lvl.Bg(l);
        }
    }
}
