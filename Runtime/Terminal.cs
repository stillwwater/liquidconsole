using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Liquid.Console
{
    public class Terminal : MonoBehaviour {
        [Header("Input")]
        [SerializeField] KeyCode toggleKey = KeyCode.F1;
        [SerializeField] string caret      = "→";
        [SerializeField] int cursorWidth   = 12;
        [SerializeField] AutoCompleteMode autoCompleteMode = default(AutoCompleteMode);
        [SerializeField] int autoCompleteItems = 16;

        [Header("Options")]
        [SerializeField] bool enableCheats      = false;
        [SerializeField] bool extraCommands     = true;
        [SerializeField] bool dontDestroyOnLoad = true;

        [Header("Text")]
        [SerializeField] Font font         = null;
        [SerializeField] float lineSpacing = 0f;
        [SerializeField] float textShadow  = 2f;

        [Header("Appearance")]
        [SerializeField] [Range(0, 1)] float maxHeight           = 0.7f;
        [SerializeField] [Range(100, 2000)] float animationSpeed = 900f;
        [SerializeField] [Range(0, 1)] float windowOpacity       = 0.95f;
        [SerializeField] [Range(0, 1)] float inputOpacity        = 0.8f;

        [Tooltip("At least 8 colors to use")]
        [SerializeField] Color[] colors = new Color[8] {
            new Color32(0x26, 0x2b, 0x3b, 0xff), // Background
            new Color32(0xe8, 0x58, 0x6d, 0xff), // Error
            new Color32(0xd7, 0xe2, 0xff, 0xff), // Input
            new Color32(0xff, 0xed, 0xcb, 0xff), // Warnings
            new Color32(0xce, 0xb3, 0xf7, 0xff), // Special
            new Color32(0x8d, 0x9a, 0xd1, 0xff),
            new Color32(0xd7, 0xe2, 0x6d, 0xff),
            new Color32(0x63, 0x6b, 0x90, 0xff), // Foreground
        };

        [Header("Interface")]
        [SerializeField] bool scrollBar    = true;
        [SerializeField] bool submitButton = true;
        [SerializeField] bool closeButton  = false;

        [Header("Prefabs")]
        [SerializeField] Canvas consoleWindow = null;
        [SerializeField] Text logItem         = null;

        public enum State { Closed, OpenMin, OpenMax }

        public enum AutoCompleteMode {
            // First token will show the autocomplete window
            // following tokens require tab to be pressed.
            FirstToken,

            // Always display autocomplete window when possible.
            Always,

            // Only display autocomplete window when tab is pressed.
            TabPress,
        }

        struct References {
            public RectTransform window;
            public RectTransform logs;
            public RectTransform bufferWindow;
            public RectTransform inputWindow;
            public RectTransform input;
            public RectTransform submitButton;
            public RectTransform closeButton;
            public RectTransform autoComplete;
        }

        static bool isInit;
        static State state;

        References refs;
        InputField input;
        float height;
        int logIndex;
        bool locked;
        bool firstToken = true;
        List<string> completeBuffer = new List<string>();

        public static bool IsOpen => state != State.Closed;

        // Opens or closes the terminal window
        public bool SetState(State next) {
            if (!isInit) {
                return false;
            }
            SwitchState(next);
            return true;
        }

        void Awake() {
            Shell.Init(math: extraCommands);
            Shell.cheats = enableCheats;
            Shell.Module(this);
            Shell.Eval("alias quit exit; alias cls clear");
        }

        void Start() {
            if (dontDestroyOnLoad) {
                if (isInit) {
                    Destroy(this);
                    return;
                }
                DontDestroyOnLoad(gameObject);
            }
            isInit = true;
            Debug.Assert(logItem, "[Terminal] Missing LogItem prefab");
            Debug.Assert(consoleWindow, "[Terminal] Missing ConsoleWindow prefab");

            var go = Instantiate(consoleWindow.gameObject, transform);
            go.name = "ConsoleWindow";

            refs = new References {
                window       = FindChild("Window"),
                bufferWindow = FindChild("BufferWindow"),
                inputWindow  = FindChild("InputWindow"),
                logs         = FindChild("Logs"),
                input        = FindChild("Input"),
                submitButton = FindChild("Submit"),
                closeButton  = FindChild("Close"),
                autoComplete = FindChild("AutoComplete"),
            };

            input = refs.input.GetComponent<InputField>();
            Debug.Assert(input);
            input.caretWidth = cursorWidth;

            height = Screen.height * maxHeight;
            refs.window.sizeDelta = new Vector2(refs.window.sizeDelta.x, height);
            refs.window.anchoredPosition = new Vector2(0f, height);

            var vlg = refs.logs.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = lineSpacing;

            if (font == null) {
                font = input.textComponent.font;
            } else {
                input.textComponent.font = font;
                logItem.font = font;
            }

            var logShadow = logItem.GetComponent<Shadow>();
            if (logShadow) {
                logShadow.effectDistance = new Vector2(0f, -textShadow);
            }

            var inputShadow = input.textComponent.GetComponent<Shadow>();
            if (inputShadow) {
                inputShadow.effectDistance = new Vector2(0f, -textShadow);
            }

            if (!scrollBar) {
                Destroy(FindChild("Scrollbar").gameObject);
                var scroll = refs.bufferWindow.GetComponent<ScrollRect>();
                scroll.verticalScrollbar = null;
            }

            if (submitButton) {
                var submit = refs.submitButton.GetComponent<Button>();
                submit.onClick.AddListener(() => EnterCommand(input.text));
            } else {
                Destroy(refs.submitButton.gameObject);
            }

            if (closeButton) {
                var close = refs.closeButton.GetComponent<Button>();
                close.onClick.AddListener(() => SwitchState(State.Closed));
            } else {
                Destroy(refs.closeButton.gameObject);
            }

            UpdateColors();

            state = State.Closed;
            refs.window.gameObject.SetActive(false);
        }

        RectTransform FindChild(string name) {
            var child = FindChild(transform, name);
            Debug.Assert(child, $"[Terminal] Missing {name}");

            var rect = child.GetComponent<RectTransform>();
            Debug.Assert(rect, $"[Terminal] {name} is missing a RectTransform");
            return rect;
        }

        Transform FindChild(Transform parent, string name) {
            if (parent == null) {
                return null;
            }
            foreach (Transform child in parent.transform) {
                if (child.name == name) {
                    return child;
                }
                var next = FindChild(child, name);
                if (next != null) {
                    return next;
                }
            }
            return null;
        }

        IEnumerator AnimateWindow(float y) {
            if (state != State.Closed) {
                refs.window.gameObject.SetActive(true);
            }

            var start = refs.window.anchoredPosition;
            var end = new Vector2(refs.window.anchoredPosition.x, y);
            float s = Mathf.Abs(1 / (end.y - start.y)) * animationSpeed;
            locked = true;

            for (float t = 0f; t <= 1f; t += Time.deltaTime * s) {
                refs.window.anchoredPosition = Vector2.Lerp(start, end, t);
                yield return null;
            }
            refs.window.anchoredPosition = end;
            locked = false;

            if (state == State.Closed) {
                refs.window.gameObject.SetActive(false);
            } else {
                ResetCursor();
            }
        }

        void UpdateColors() {
            Image im;
            var bg = colors[0];
            var alt = colors[6];

            im = refs.bufferWindow.GetComponent<Image>();
            im.color = new Color(bg.r, bg.g, bg.b, windowOpacity);

            im = refs.inputWindow.GetComponent<Image>();
            im.color = new Color(bg.r, bg.g, bg.b, inputOpacity);

            im = refs.autoComplete.GetComponent<Image>();
            im.color = alt;

            input.textComponent.color = colors[2];
        }

        void RenderText(string str, int color) {
            if (str.Length <= Shell.columns && !str.Contains("\n")) {
                CreateLog(refs.logs, str, color);
                return;
            }
            // Handle multiline input
            for (int i = 0; i < str.Length;) {
                int offset = 0;
                int length = Math.Min(str.Length - i, Shell.columns);
                int lf = str.IndexOf('\n', i);
                if (lf >= 0) {
                    length = lf - i;
                    offset = 1;
                }
                CreateLog(refs.logs, str.Substring(i, length), color);
                i += length + offset;
            }
        }

        void CreateLog(RectTransform parent, string str, int color) {
            Debug.Assert(color >= 0 && color < colors.Length);
            var go = Instantiate(logItem, parent);
            var text = go.GetComponent<Text>();

            text.text = str;
            text.color = colors[color];
            text.name = (logIndex++).ToString("x3");
        }

        void ClearLogs(RectTransform parent) {
            foreach (Transform child in parent) {
                Destroy(child.gameObject);
            }
        }

        void EnterCommand(string str) {
            RenderText(string.Concat(caret, " ", str), 5);
            Shell.Eval(str);
            ResetCursor();
            input.text = "";
        }

        void ResetCursor() {
            input.ActivateInputField();
            input.Select();
            firstToken = true;
        }

        void SwitchState(State next) {
            if (next == state) {
                next = State.Closed;
            }
            if (next == State.Closed && state != State.Closed) {
                state = State.Closed;
                StartCoroutine(AnimateWindow(height));
                return;
            }
            if (next == State.OpenMin) {
                state = State.OpenMin;
                StartCoroutine(AnimateWindow(height * 0.5f));
                return;
            }
            state = State.OpenMax;
            StartCoroutine(AnimateWindow(0f));
        }

        void ShowCompletions() {
            Shell.CompleteSymbol(input.text, completeBuffer);
            ClearLogs(refs.autoComplete);

            if (completeBuffer.Count < 1) {
                HideCompletions();
                return;
            }
            refs.autoComplete.gameObject.SetActive(true);

            for (int i = 0; i < completeBuffer.Count; ++i) {
                if (i > autoCompleteItems) {
                    CreateLog(refs.autoComplete,
                              $"+{completeBuffer.Count - autoCompleteItems}", 7);
                    break;
                }
                CreateLog(refs.autoComplete, completeBuffer[i], 7);
            }
        }

        void AutoComplete() {
            input.text = Shell.CompleteSymbol(input.text, completeBuffer);

            if (completeBuffer.Count <= 1) {
                ResetCursor();
                input.caretPosition = input.text.Length;
            }
        }

        void HideCompletions() {
            refs.autoComplete.gameObject.SetActive(false);
        }

        void Update() {
            if (locked) {
                return;
            }

            if (Input.GetKeyDown(toggleKey)) {
                HideCompletions();
                if (Input.GetKey(KeyCode.LeftShift)) {
                    SwitchState(State.OpenMin);
                    return;
                }
                SwitchState(State.OpenMax);
                return;
            }

            if (Input.GetKeyDown(KeyCode.Return)
                || Input.GetKeyDown(KeyCode.KeypadEnter)) {
                HideCompletions();
                EnterCommand(input.text);
                return;
            }

            if ((autoCompleteMode == AutoCompleteMode.Always
                || (autoCompleteMode == AutoCompleteMode.FirstToken && firstToken))
                && Input.anyKeyDown) {
                ShowCompletions();
            }

            if (Input.GetKeyDown(KeyCode.Backspace)) {
                HideCompletions();
            }

            if (Input.GetKeyDown(KeyCode.Tab)) {
                ShowCompletions();
                AutoComplete();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Space)) {
                HideCompletions();
                firstToken = false;
            }

            if (Shell.buffer.Count == 0) {
                return;
            }

            foreach (var ln in Shell.buffer) {
                RenderText(ln.value, ln.color);
            }
            Shell.buffer.Clear();
        }

        [Command]
        void Clear() {
            ClearLogs(refs.logs);
        }

        [Command]
        void Exit() {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
} // namespace Liquid.Console
