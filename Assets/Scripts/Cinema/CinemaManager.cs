// =============================================================================
//  CinemaManager.cs
//
//  Top-level cinema flow: which theater (if any) is active, and the lobby
//  spawn point. This is the single choke point that enforces "only the active
//  theater's BrowserPlane is ever live" - TheaterManager/MovieScreen don't
//  decide that for themselves, they just do what they're told.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

public class CinemaManager : MonoBehaviour
{
    public static CinemaManager Instance { get; private set; }

    [Tooltip("Where the player starts, and returns to on ExitToLobby().")]
    public Transform lobbySpawnPoint;

    public List<TheaterManager> theaters = new List<TheaterManager>();

    [Header("Lobby Floor")]
    [Tooltip("Each theater only builds a floor under its OWN seating area - " +
             "nothing else in the cinema has ground. This builds one big floor " +
             "under everything (the lobby, the space between theaters, and " +
             "underneath every theater too) so there's never a gap to fall " +
             "through no matter where a theater ends up being placed.")]
    public bool buildLobbyFloor = true;

    [Tooltip("World-space centre of the lobby floor. Size it and centre it to " +
             "comfortably cover the lobby spawn point and every theater.")]
    public Vector3 lobbyFloorCenter = new Vector3(30f, 0f, 17f);

    [Tooltip("Width (X) and depth (Z) of the lobby floor, in metres.")]
    public Vector2 lobbyFloorSize = new Vector2(100f, 60f);

    [Tooltip("Build 4 walls and a ceiling around the lobby floor - without this " +
             "there is only a floor with no enclosure at all.")]
    public bool buildLobbyWalls = true;

    [Tooltip("Wall/ceiling height, in metres, and how far outside lobbyFloorSize " +
             "the walls sit (a small margin keeps them clear of any geometry " +
             "right at the floor's edge).")]
    public float lobbyWallHeight = 8f;
    public float lobbyWallMargin = 2f;
    public float lobbyWallThickness = 0.3f;

    public TheaterManager ActiveTheater { get; private set; }
    public MovieScreen ActiveScreen => ActiveTheater != null ? ActiveTheater.screen : null;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[Cinema] More than one CinemaManager in the scene - " +
                             "keeping the first, destroying this one.");
            Destroy(this);
            return;
        }
        Instance = this;

        if (buildLobbyFloor) BuildLobbyFloor();
        if (buildLobbyWalls) BuildLobbyWalls();
    }

    private void BuildLobbyFloor()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "LobbyFloor";
        floor.transform.SetParent(transform, false);
        floor.transform.position = lobbyFloorCenter;
        // Unity's Plane primitive is 10x10 local units.
        floor.transform.localScale = new Vector3(lobbyFloorSize.x / 10f, 1f, lobbyFloorSize.y / 10f);

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader != null)
        {
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", new Color(0.08f, 0.08f, 0.09f));
            floor.GetComponent<Renderer>().sharedMaterial = mat;
        }
        else
        {
            Debug.LogWarning("[Cinema] URP Lit shader not found - LobbyFloor will render " +
                             "with whatever Unity's default material is.");
        }
    }

    /// <summary>Encloses the lobby floor with 4 walls and a ceiling - a plain box
    /// around lobbyFloorSize, offset out by lobbyWallMargin. This is the ONLY
    /// place lobby walls come from; TheaterBuilder only ever builds inside a
    /// theater's own footprint.</summary>
    private void BuildLobbyWalls()
    {
        float halfW = lobbyFloorSize.x * 0.5f + lobbyWallMargin;
        float halfD = lobbyFloorSize.y * 0.5f + lobbyWallMargin;
        Vector3 c = lobbyFloorCenter;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        Material wallMat = null;
        if (shader != null)
        {
            wallMat = new Material(shader);
            wallMat.SetColor("_BaseColor", new Color(0.12f, 0.12f, 0.14f));
        }
        else
        {
            Debug.LogWarning("[Cinema] URP Lit shader not found - lobby walls will render " +
                             "with whatever Unity's default material is.");
        }

        // North/south walls run along X, east/west walls run along Z.
        BuildWall("LobbyWall_North", new Vector3(c.x, c.y + lobbyWallHeight * 0.5f, c.z + halfD),
                 new Vector3(halfW * 2f, lobbyWallHeight, lobbyWallThickness), wallMat);
        BuildWall("LobbyWall_South", new Vector3(c.x, c.y + lobbyWallHeight * 0.5f, c.z - halfD),
                 new Vector3(halfW * 2f, lobbyWallHeight, lobbyWallThickness), wallMat);
        BuildWall("LobbyWall_East", new Vector3(c.x + halfW, c.y + lobbyWallHeight * 0.5f, c.z),
                 new Vector3(lobbyWallThickness, lobbyWallHeight, halfD * 2f), wallMat);
        BuildWall("LobbyWall_West", new Vector3(c.x - halfW, c.y + lobbyWallHeight * 0.5f, c.z),
                 new Vector3(lobbyWallThickness, lobbyWallHeight, halfD * 2f), wallMat);
        BuildWall("LobbyCeiling", new Vector3(c.x, c.y + lobbyWallHeight, c.z),
                 new Vector3(halfW * 2f, lobbyWallThickness, halfD * 2f), wallMat);
    }

    private void BuildWall(string wallName, Vector3 center, Vector3 size, Material mat)
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = wallName;
        wall.transform.SetParent(transform, false);
        wall.transform.position = center;
        wall.transform.localScale = size;
        if (mat != null) wall.GetComponent<Renderer>().sharedMaterial = mat;
    }

    private void Start()
    {
        if (lobbySpawnPoint != null && PlayerManager.Instance != null)
            PlayerManager.Instance.Teleport(lobbySpawnPoint.position, lobbySpawnPoint.rotation);
    }

    /// <summary>Deactivates whichever theater is currently active (if any) and
    /// activates this one. Never more than one theater's BrowserPlane is live
    /// at once, regardless of how many theaters exist in the cinema.</summary>
    public void EnterTheater(TheaterManager theater)
    {
        if (theater == null || theater == ActiveTheater) return;

        if (ActiveTheater != null) ActiveTheater.Deactivate();

        ActiveTheater = theater;
        theater.Activate();
    }

    /// <summary>Deactivates the active theater (if any), stands the player up,
    /// and returns them to the lobby spawn point.</summary>
    public void ExitToLobby()
    {
        if (ActiveTheater != null)
        {
            ActiveTheater.Deactivate();
            ActiveTheater = null;
        }
        if (PlayerManager.Instance != null)
        {
            PlayerManager.Instance.StandUp();
            if (lobbySpawnPoint != null)
                PlayerManager.Instance.Teleport(lobbySpawnPoint.position, lobbySpawnPoint.rotation);
        }
    }
}
