using UnityEngine;

public class QuitApplication : MonoBehaviour
{
    /// <summary>
    /// Quits the game. In the Editor it just stops Play mode.
    /// Hook this up to a UI Button OnClick.
    /// </summary>
    public void QuitGame()
    {
        Application.Quit();
    }
}
