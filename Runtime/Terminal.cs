using UnityEngine;
using UnityEngine.UI;

namespace Liquid.Console
{
    public class Terminal : MonoBehaviour {
        [SerializeField] InputField input = null;

        void Start() {
            Shell.Init(math: true);
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
    }
} // namespace Liquid.Console
