using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bwos
{
    /// <summary>Speech is data, never executable code. Only these explicit intents can run.</summary>
    internal sealed class VoiceCommand
    {
        public string Action;
        public string Value;
        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> { { "action", Action }, { "value", Value ?? "" } };
        }
    }

    internal static class VoiceCommands
    {
        public static string Normalize(string text)
        {
            return Regex.Replace((text ?? "").ToLowerInvariant().Trim(), @"\s+", " ").Trim(' ', '.', ',', '!', '?', '।');
        }

        // Ordinary words already in both models, instead of the difficult-to-pronounce app name.
        public static string StripWake(string text, out bool woke)
        {
            // Dictation frequently inserts punctuation between the greeting and name.
            // Keep an explicit name and a word boundary; do not fuzzy-match ordinary speech.
            var m = Regex.Match(Normalize(text), @"^(?:(?:hey|hello|हे|हेय|हेलो)[,，:!.।]?\s+(?:console|consul|counsel|con\s+sole|कंसोल|कन्सोल|assistant|असिस्टेंट))(?:[,，:!.।]\s*|\s+|$)");
            woke = m.Success;
            return m.Success ? Normalize(text).Substring(m.Length).Trim() : Normalize(text);
        }

        public static VoiceCommand Parse(string text)
        {
            string t = Normalize(text);
            bool ignored;
            t = StripWake(t, out ignored);
            t = Regex.Replace(t, @"^(?:(?:can|could|would) you\s+(?:please\s+)?|i want (?:you )?to\s+|please\s+)", "");
            var exact = new Dictionary<string, string> {
                { "up", "up" }, { "move up", "up" }, { "ऊपर", "up" },
                { "down", "down" }, { "scroll down", "down" }, { "नीचे", "down" },
                { "left", "left" }, { "बाएं", "left" }, { "बायें", "left" }, { "बाएँ", "left" },
                { "right", "right" }, { "दाएं", "right" }, { "दायें", "right" }, { "दाएँ", "right" },
                { "select", "accept" }, { "okay", "accept" }, { "ok", "accept" }, { "चुनो", "accept" },
                { "back", "back" }, { "go back", "back" }, { "वापस", "back" }, { "पीछे", "back" },
                { "cancel", "back" }, { "कैंसिल", "back" },
                { "home", "home" }, { "go home", "home" }, { "घर चलो", "home" }, { "होम", "home" },
                { "pause", "pause" }, { "pause video", "pause" }, { "रुको", "pause" }, { "रोक दो", "pause" },
                { "resume", "resume" }, { "resume video", "resume" }, { "जारी रखो", "resume" },
                { "resume game", "resumeGame" }, { "गेम जारी रखो", "resumeGame" },
                { "volume up", "volup" }, { "आवाज़ बढ़ाओ", "volup" }, { "आवाज बढ़ाओ", "volup" },
                { "volume down", "voldown" }, { "आवाज़ कम करो", "voldown" }, { "आवाज कम करो", "voldown" },
                { "mute", "mute" }, { "आवाज़ बंद करो", "mute" }, { "आवाज बंद करो", "mute" },
                { "show numbers", "numbers" }, { "नंबर दिखाओ", "numbers" }, { "नम्बर दिखाओ", "numbers" },
                { "hide numbers", "hideNumbers" }, { "नंबर हटाओ", "hideNumbers" },
                { "voice help", "help" }, { "what can i say", "help" }, { "मदद", "help" },
                { "show assistant", "showAssistant" }, { "assistant", "showAssistant" }, { "असिस्टेंट दिखाओ", "showAssistant" },
                { "stop listening", "sleepVoice" }, { "go to sleep", "sleepVoice" }, { "सुनना बंद करो", "sleepVoice" }
            };
            Add(exact, "volup", "go volume up|turn it up|turn up the volume|increase volume|increase the volume|louder|make it louder|आवाज तेज करो|वॉल्यूम बढ़ाओ");
            Add(exact, "voldown", "turn it down|turn down the volume|decrease volume|decrease the volume|quieter|make it quieter|वॉल्यूम कम करो");
            Add(exact, "mute", "mute sound|mute audio|mute the volume|sound off|चुप करो");
            Add(exact, "unmute", "unmute|unmute sound|unmute audio|sound on|आवाज चालू करो|आवाज़ चालू करो");
            Add(exact, "pause", "pause music|pause playback|pause the video|pause the music|संगीत रोक दो|वीडियो रोक दो");
            Add(exact, "resume", "continue|continue playing|resume music|resume playback|play music|play video|play the video|संगीत चलाओ|वीडियो चलाओ");
            Add(exact, "togglePlayback", "play pause|toggle playback|toggle play pause");
            Add(exact, "stopPlayback", "stop music|stop video|stop playback|close player|close the player|संगीत बंद करो|वीडियो बंद करो");
            Add(exact, "reopenPlayer", "reopen player|show player|return to player|back to music|back to video|प्लेयर खोलो");
            Add(exact, "playerControls", "now playing|show now playing|show playback controls|show player controls|प्लेयर कंट्रोल दिखाओ");
            Add(exact, "restartMedia", "restart video|restart song|play from the beginning|start from the beginning|शुरू से चलाओ");
            Add(exact, "seekForward", "fast forward|skip forward|forward ten seconds|आगे बढ़ाओ");
            Add(exact, "seekBack", "rewind|skip back|back ten seconds|पीछे करो");
            Add(exact, "nextMedia", "next video|next song|next track|skip this song|अगला गाना|अगला वीडियो");
            Add(exact, "previousMedia", "previous video|previous song|previous track|पिछला गाना");
            Add(exact, "keyboard", "show keyboard|open keyboard|कीबोर्ड खोलो");
            Add(exact, "reloadPage", "reload page|refresh page|retry youtube|reload youtube|try again|पेज रीलोड करो");
            Add(exact, "subtitlesOn", "subtitles on|turn on subtitles|captions on|सबटाइटल चालू करो");
            Add(exact, "subtitlesOff", "subtitles off|turn off subtitles|captions off|सबटाइटल बंद करो");
            Add(exact, "accept", "click|enter|confirm|press enter|चुन लो");
            Add(exact, "home", "go to home|take me home|return home|home screen|होम पर जाओ");
            Add(exact, "help", "help|show voice commands|what can you do|show voice help");
            string action;
            string simple = Regex.Replace(t, @"(?:\s+(?:now|please))+$", "");
            if (exact.TryGetValue(simple, out action)) return Make(action, action == "seekForward" || action == "seekBack" ? "10" : "");
            var numeric = Regex.Match(simple, @"^(?:set (?:the )?volume(?: to)?|volume|वॉल्यूम|आवाज|आवाज़)\s+(.+?)(?:\s+(?:percent|प्रतिशत))?(?: कर दो| करो)?$");
            int amount;
            if (numeric.Success && Number(numeric.Groups[1].Value, out amount) && amount >= 0 && amount <= 100) return Make("volumeSet", amount.ToString());
            numeric = Regex.Match(simple, @"^(increase|decrease|raise|lower)(?: the)? volume by (.+?)(?: percent)?$");
            if (numeric.Success && Number(numeric.Groups[2].Value, out amount) && amount > 0 && amount <= 100) return Make("volumeChange", ((numeric.Groups[1].Value == "decrease" || numeric.Groups[1].Value == "lower") ? -amount : amount).ToString());
            numeric = Regex.Match(simple, @"^(?:skip |seek |go )?(forward|back|backward|rewind) (.+?) (?:seconds?|सेकंड)$");
            if (numeric.Success && Number(numeric.Groups[2].Value, out amount) && amount > 0 && amount <= 100) return Make(numeric.Groups[1].Value == "forward" ? "seekForward" : "seekBack", amount.ToString());
            var m = Regex.Match(t, @"^(?:search (?:youtube|you tube) for|search on (?:youtube|you tube) for|(?:youtube|you tube) search|(?:यूट्यूब|यू ट्यूब) पर खोजो|(?:यूट्यूब|यू ट्यूब) खोजो)\s+(.+)$");
            if (m.Success) return Make("youtubeSearch", m.Groups[1].Value);
            m = Regex.Match(t, @"^(?:search(?: for)?|find|play)\s+(.+?)\s+on (?:youtube|you tube)$");
            if (m.Success) return Make("youtubeSearch", m.Groups[1].Value);
            m = Regex.Match(t, @"^(?:यूट्यूब|यू ट्यूब) पर (.+?) (?:खोजो|ढूंढो|ढूँढो)$");
            if (m.Success) return Make("youtubeSearch", m.Groups[1].Value);
            m = Regex.Match(t, @"^(?:please\s+)?(?:search(?: for)?|find(?: a video (?:of|about))?|खोजो|ढूंढो|ढूँढो)\s+(.+)$");
            if (!m.Success) m = Regex.Match(t, @"^(.+?) (?:खोजो|ढूंढो|ढूँढो)$");
            if (m.Success) return Make("search", m.Groups[1].Value);
            m = Regex.Match(t, @"^(?:select(?: number)?|choose|number|नंबर|नम्बर)\s+(.+?)(?: चुनो)?$");
            if (m.Success)
            {
                int n;
                if (!Number(m.Groups[1].Value, out n)) return null;
                if (n > 0 && n <= 99) return Make("selectNumber", n.ToString());
                return null;
            }
            m = Regex.Match(t, @"^(?:browse|explore)\s+(.+)$");
            if (!m.Success) m = Regex.Match(t, @"^(.+?) (?:ब्राउज़ करो|ब्राउज करो)$");
            if (m.Success) return Make("browseService", m.Groups[1].Value);
            m = Regex.Match(t, @"^open (.+?) (?:app|website)$");
            if (m.Success) return Make("openServiceApp", m.Groups[1].Value);
            m = Regex.Match(t, @"^(?:please\s+)?(?:open|show|launch|play|खोलो|चलाओ)\s+(.+)$");
            if (!m.Success) m = Regex.Match(t, @"^(.+?) (?:खोलो|खोल दो|चलाओ|दिखाओ)$");
            if (m.Success) return Make("open", Regex.Replace(m.Groups[1].Value, @"(?:\s+(?:now|please))+$", ""));
            return null;
        }

        private static VoiceCommand Make(string action, string value)
        {
            return new VoiceCommand { Action = action, Value = value };
        }
        private static void Add(Dictionary<string, string> commands, string action, string phrases)
        {
            foreach (string phrase in phrases.Split('|')) commands[phrase] = action;
        }

        private static bool Number(string text, out int value)
        {
            if (text.StartsWith("-")) { value = 0; return false; }
            string v = text.Replace('-', ' ').Replace("पाँच", "पांच");
            if (v == "one hundred" || v == "hundred" || v == "सौ") { value = 100; return true; }
            var hindi = new Dictionary<string, int> { { "पच्चीस", 25 }, { "तीस", 30 }, { "पैंतीस", 35 }, { "चालीस", 40 }, { "पैंतालीस", 45 }, { "पचास", 50 }, { "पचपन", 55 }, { "साठ", 60 }, { "पैंसठ", 65 }, { "सत्तर", 70 }, { "पचहत्तर", 75 }, { "अस्सी", 80 }, { "पचासी", 85 }, { "नब्बे", 90 }, { "पचानवे", 95 } };
            if (hindi.TryGetValue(v, out value)) return true;
            for (int i = 0; i < 10; i++) v = v.Replace((char)('०' + i), (char)('0' + i));
            if (int.TryParse(v, out value)) return true;
            string[] en = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
            string[] hi = { "शून्य", "एक", "दो", "तीन", "चार", "पांच", "छह", "सात", "आठ", "नौ", "दस", "ग्यारह", "बारह", "तेरह", "चौदह", "पंद्रह", "सोलह", "सत्रह", "अठारह", "उन्नीस", "बीस" };
            value = Array.IndexOf(en, v);
            if (value < 0) value = Array.IndexOf(hi, v);
            if (value >= 0) return true;
            string[] tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
            string[] parts = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { value = 0; return false; }
            int ten = Array.IndexOf(tens, parts[0]);
            if (ten >= 2 && parts.Length <= 2)
            {
                int unit = parts.Length == 1 ? 0 : Array.IndexOf(en, parts[1]);
                if (unit >= 0 && unit < 10) { value = ten * 10 + unit; return true; }
            }
            // Spoken digit pairs also work: "one two" / "एक दो" means tile 12.
            if (parts.Length == 2)
            {
                int a = Array.IndexOf(en, parts[0]), b = Array.IndexOf(en, parts[1]);
                if (a < 0) a = Array.IndexOf(hi, parts[0]);
                if (b < 0) b = Array.IndexOf(hi, parts[1]);
                if (a >= 1 && a < 10 && b >= 0 && b < 10) { value = a * 10 + b; return true; }
            }
            value = 0; return false;
        }
    }

    /// <summary>Direct mode is foreground-only; wake mode permits a short background session.</summary>
    internal sealed class VoiceSession
    {
        private DateTime until;
        private readonly object gate = new object();
        private bool sleeping;
        public bool Sleeping { get { lock (gate) return sleeping; } }
        public bool Active(DateTime now) { lock (gate) return now < until; }
        public VoiceCommand Accept(string text, string mode, bool foreground, DateTime now)
        {
            lock (gate)
            {
                bool woke;
                string rest = VoiceCommands.StripWake(text, out woke);
                if (woke) { sleeping = false; until = now.AddSeconds(12); }
                if (Sleeping || (mode == "direct" ? !foreground : mode == "wake" && !Active(now))) return null;
                VoiceCommand command = VoiceCommands.Parse(rest);
                if (command != null)
                {
                    if (command.Action == "sleepVoice") { sleeping = true; until = DateTime.MinValue; }
                    else until = now.AddSeconds(12);
                }
                return command;
            }
        }
    }
}
