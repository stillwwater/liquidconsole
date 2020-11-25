using UnityEngine;
using UnityEngine.UI;

namespace Liquid.Console
{
    public class Terminal : MonoBehaviour {
        [SerializeField] InputField input = null;
        [ConVar] int variable;

        void Start() {
            Shell.Init(math: true);
            Shell.Module(this);
            Shell.Eval("alias quit exit");

            input.caretWidth = 8;
            input.Select();
            input.ActivateInputField();
        }

        void Update() {
            if (Input.GetKeyDown(KeyCode.Return)) {
                Shell.Eval(input.text);
                input.text = "";
                input.Select();
                input.ActivateInputField();
            }

            if (Shell.buffer.Count == 0)
                return;

            foreach (var line in Shell.buffer) {
                switch (line.color) {
                    case 1: Debug.LogError(line.value); break;
                    case 2: Debug.LogWarning(line.value); break;

                    case 7:
                    default: Debug.Log(line.value); break;
                }
            }
            Shell.buffer.Clear();
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
