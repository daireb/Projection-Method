using System.Collections;
using Unity.VisualScripting;
using UnityEngine;
using System;
using UnityEditor;
using UnityEngine.UIElements;

[System.Serializable]
public class IntArrayData
{
    public int[] myArray;
}

public class FDM_Controller : MonoBehaviour
{
    public int domainWidth = 256;
    public int domainHeight = 256;
    public bool autoPlay = true;
    [Range(1f,1.99f)] public float incompressibilityOverRelaxation = 1.8f;

    [Range(0f,1f)] public float pressureRelaxationFactor = 0.99f;

    public int simulationIterationsPerFrame = 10;
    public int incomressibilityIterationsPerSimulationTick = 10;

    public ComputeShader solveMomentumShader;
    private int momentumKernelId;
    public ComputeShader solveIncompressiblityShader;
    private int incompressibilityKernelId;
    private int dampPressureKernelId;

    public ComputeShader streamlinesShader;
    private int streamlinesKernelId;

    public ComputeShader dragShader;
    private int dragKernelId;

    public ComputeShader domainShader;
    private int domainKernelId;

    public ComputeShader renderShader;
    private int renderKernelId;

    public Material renderMaterial;

    public float mu = 0.5f; // Kinematic viscosity

    private int numLines = 128;
    private int streamlineNodeAmount = 100;
    public bool streamLinesEnabled = true;

    int currentBufferId = 0;
    ComputeBuffer[] uBuffers;
    ComputeBuffer[] vBuffers;
    ComputeBuffer pBuffer;
    ComputeBuffer domainBuffer;

    int currentRenderMode = 1;
    public bool renderVelocityEnabled = true;

    public float h = 0.1f;

    private RenderTexture textureOut;

    private int currentScenario;
    private float baseRenderSpeed;

    public float gradientTightness = 1f;
    public float gradientRangeMult = 1f;

    void swapBuffers() {
        currentBufferId = (currentBufferId+1) % 2;
    }

    void screenshot() {
        string fileName = "Screenshot.png";
        ScreenCapture.CaptureScreenshot(fileName, 1);

        string rootPath = Application.dataPath.Substring(0, Application.dataPath.Length - "/Assets".Length);
        string filePath = rootPath + "/" + fileName;
        Debug.Log("Captured screenshot. Storing at " + filePath);
    }

    void render() {
        renderShader.SetBuffer(renderKernelId,"u",uBuffers[currentBufferId]);
        renderShader.SetBuffer(renderKernelId,"v",vBuffers[currentBufferId]);
        renderShader.SetTexture(renderKernelId,"textureOut",textureOut);

        renderShader.SetInt("renderMode",currentRenderMode);
        renderShader.SetFloat("gradientTightness",gradientTightness);
        renderShader.SetFloat("gradientRangeMult",gradientRangeMult);

        if (renderVelocityEnabled) {
            renderShader.SetFloat("baseSpeed",baseRenderSpeed);
        } else {
            renderShader.SetFloat("baseSpeed",0f);
        }

        renderShader.Dispatch(renderKernelId, domainWidth/8, domainHeight/8, 1);
    }

    void drawStreamLines() {
        streamlinesShader.SetBuffer(streamlinesKernelId,"u",uBuffers[currentBufferId]);
        streamlinesShader.SetBuffer(streamlinesKernelId,"v",vBuffers[currentBufferId]);

        streamlinesShader.SetTexture(streamlinesKernelId,"targetTexture",textureOut);
        streamlinesShader.SetInt("numLines",numLines);

        streamlinesShader.Dispatch(streamlinesKernelId, numLines, 1, 1);
    }

    void solveMomentum(float dt) {
        int nextBufferId = (currentBufferId + 1) % 2;

        solveMomentumShader.SetBuffer(momentumKernelId,"u",uBuffers[currentBufferId]);
        solveMomentumShader.SetBuffer(momentumKernelId,"v",vBuffers[currentBufferId]);
        solveMomentumShader.SetBuffer(momentumKernelId,"uNew",uBuffers[nextBufferId]);
        solveMomentumShader.SetBuffer(momentumKernelId,"vNew",vBuffers[nextBufferId]);

        solveMomentumShader.SetFloat("dt",dt);
        solveMomentumShader.SetFloat("mu",mu);

        solveMomentumShader.Dispatch(momentumKernelId, domainWidth/8, domainHeight/8, 1);
    }

    void setVelocity(Vector2 from, Vector2 to, Vector2 vel) {
        dragShader.SetBuffer(dragKernelId,"u",uBuffers[currentBufferId]);
        dragShader.SetBuffer(dragKernelId,"v",vBuffers[currentBufferId]);

        dragShader.SetVector("dragStart",from);
        dragShader.SetVector("dragEnd",to);
        dragShader.SetVector("setVel",vel);
        dragShader.SetFloat("dragRadius",5);

        dragShader.Dispatch(dragKernelId, domainWidth/8, domainHeight/8, 1);
    }

    void setDomain(Vector2 from, Vector2 to, int domainSet) {
        domainShader.SetBuffer(domainKernelId,"u",uBuffers[currentBufferId]);
        domainShader.SetBuffer(domainKernelId,"v",vBuffers[currentBufferId]);

        domainShader.SetVector("dragStart",from);
        domainShader.SetVector("dragEnd",to);
        domainShader.SetInt("domainSet",domainSet);
        domainShader.SetFloat("dragRadius",5);

        domainShader.Dispatch(domainKernelId, domainWidth/8, domainHeight/8, 1);
    }

    void dampenPressureBuffer(float damping) {
        solveIncompressiblityShader.SetFloat("pressureDamping", damping);
        solveIncompressiblityShader.Dispatch(dampPressureKernelId, domainWidth/8, domainHeight/8, 1);
    }

    void solveIncompressiblity(float dt) {
        int nextBufferId = (currentBufferId + 1) % 2;

        solveIncompressiblityShader.SetBuffer(incompressibilityKernelId,"u",uBuffers[currentBufferId]);
        solveIncompressiblityShader.SetBuffer(incompressibilityKernelId,"v",vBuffers[currentBufferId]);

        solveIncompressiblityShader.SetBuffer(incompressibilityKernelId,"uNew",uBuffers[nextBufferId]);
        solveIncompressiblityShader.SetBuffer(incompressibilityKernelId,"vNew",vBuffers[nextBufferId]);
        
        solveIncompressiblityShader.SetFloat("relaxationFactor",incompressibilityOverRelaxation);
        solveIncompressiblityShader.SetFloat("dt",dt);

        solveIncompressiblityShader.Dispatch(incompressibilityKernelId, domainWidth/8, domainHeight/8, 1);
    }

    Vector2 lastMousePos;
    int settingDomainTo = -1;
    void handleUserDragging(float dt, bool isSimRunning) {
        Vector3 mouseWorldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector2 localPos = transform.InverseTransformPoint(mouseWorldPos);

        Vector2 mouseDomainPos = (localPos + 0.5f*Vector2.one) * new Vector2(domainWidth,domainHeight);

        if (lastMousePos.x >= 0 && lastMousePos.y >= 0 && lastMousePos.x <= domainWidth && lastMousePos.y <= domainHeight) {
            if (Input.GetMouseButton(0) && isSimRunning) {
                Vector2 vel = (mouseDomainPos - lastMousePos)/dt*h;
                setVelocity(lastMousePos, mouseDomainPos, vel);
            }

            if (Input.GetMouseButton(1)) {
                if (settingDomainTo == -1) { // Wasn't setting - invert what is currently moused over
                    int[] domain = new int[domainWidth*domainHeight];
                    domainBuffer.GetData(domain);

                    Vector2Int cellPos = new Vector2Int((int)MathF.Floor(mouseDomainPos.x), (int)MathF.Floor(mouseDomainPos.y));
                    int selecting = domain[cellPos.x + cellPos.y * domainWidth];
                    settingDomainTo = 1 - selecting;
                }

                setDomain(lastMousePos, mouseDomainPos, settingDomainTo);
            } else {
                settingDomainTo = -1;
            }
        }

        lastMousePos = mouseDomainPos;
    }

    void stepSimulation(float dt) {
        dampenPressureBuffer(pressureRelaxationFactor);

        solveMomentum(dt);
        swapBuffers();

        for (int i = 0; i < incomressibilityIterationsPerSimulationTick; i++) {
            solveIncompressiblity(dt);
            swapBuffers();
        }
    }

    void saveDomain() {
        int[] domain = new int[domainHeight*domainWidth];
        domainBuffer.GetData(domain);
        DomainSaverLoader.SaveData(domain, "testDomain.json");
    }

    void fixDomain() { // Fixes weird pressure issue
        int[] isInDomain = new int[domainHeight*domainWidth];
        float[] u = new float[domainHeight*domainWidth];
        float[] v = new float[domainHeight*domainWidth];

        domainBuffer.GetData(isInDomain);
        uBuffers[currentBufferId].GetData(u);
        vBuffers[currentBufferId].GetData(v);

        for (int i = 1; i < domainWidth-1; i++) {
            for (int j = 1; j < domainHeight-1; j++) {
                if (isInDomain[i + j*domainWidth] == 0) {
                    u[i + j*domainWidth] = 0;
                    v[i + j*domainWidth] = 0;
                    u[(i+1) + j*domainWidth] = 0;
                    v[i + (j+1)*domainWidth] = 0;
                }
            }
        }

        domainBuffer.SetData(isInDomain);
        
        for (int i = 0; i < 2; i++) {
            uBuffers[i].SetData(u);
            vBuffers[i].SetData(v);
        }
    }

    void loadDomainFromFile(string fileName) {
        int[] isInDomain = DomainSaverLoader.LoadData(fileName);
        
        float[] u = new float[domainHeight*domainWidth];
        float[] v = new float[domainHeight*domainWidth];

        uBuffers[currentBufferId].GetData(u);
        vBuffers[currentBufferId].GetData(v);

        for (int i = 1; i < domainWidth-1; i++) {
            for (int j = 1; j < domainHeight-1; j++) {
                if (isInDomain[i + j*domainWidth] == 0) {
                    u[i + j*domainWidth] = 0;
                    v[i + j*domainWidth] = 0;
                    u[(i+1) + j*domainWidth] = 0;
                    v[i + (j+1)*domainWidth] = 0;
                }
            }
        }

        domainBuffer.SetData(isInDomain);
        
        for (int i = 0; i < 2; i++) {
            uBuffers[i].SetData(u);
            vBuffers[i].SetData(v);
        }
    }

    void setRenderMode(int renderMode) {
        currentRenderMode = renderMode;
    }

    void loadScenario(int scenarioId) {
        // Initialising fields
        float[] u = new float[domainWidth*domainHeight];
        float[] v = new float[domainWidth*domainHeight];
        float[] p = new float[domainWidth*domainHeight];
        int[] isInDomain = new int[domainWidth*domainHeight];

        // Filling with empty data
        for (int i = 0; i < domainWidth; i++) {
            for (int j = 0; j < domainHeight; j++) {
                u[i + j * domainWidth] = 0;
                v[i + j * domainWidth] = 0;

                p[i + j * domainWidth] = 0;

                isInDomain[i + j * domainWidth] = 1;
            }
        }

        // Boundary conditions
        for (int i = 0; i < domainWidth; i++) {
            isInDomain[i + 0 * domainWidth] = 0;
            isInDomain[i + (domainHeight-1) * domainWidth] = 0;
        }
        for (int j = 0; j < domainHeight; j++) {
            isInDomain[0 + j * domainWidth] = 0;
            isInDomain[domainWidth-1 + j * domainWidth] = 0;
        }

        // Scenario specifics

        baseRenderSpeed = 0;

        if (scenarioId == 2) { // Wind tunnel
            for (int i = 0; i < domainWidth; i++) {
                for (int j = 0; j < domainHeight; j++) {
                    u[i + j * domainWidth] = 10;
                }
            }

            baseRenderSpeed = 10;
        } else if (scenarioId == 3) { // Wind pipe
            for (int i = 0; i < domainWidth; i++) {
                for (int j = 0; j < domainHeight; j++) {
                    if (j > domainHeight * .4f && j < domainHeight * .6f) {
                        u[i + j * domainWidth] = 15;
                    }
                }
            }
        } else if (scenarioId == 4) { // Laminar vs Turbulent Flow
            float pipe_size = 0.55f;

            for (int j = 0; j < domainHeight; j++) {
                float y = (j+0.5f)/domainHeight;

                if (MathF.Abs(y-0.755f) <= pipe_size*.245) {
                    u[1 + j * domainWidth] = 25;
                    u[domainWidth-1 + j * domainWidth] = 25;
                } else if (MathF.Abs(y-0.245f) <= pipe_size*.245) {
                    u[1 + j * domainWidth] = 5f;
                    u[domainWidth-1 + j * domainWidth] = 5f;
                } else if (j > domainHeight * .49f && j < domainHeight * .51f) {
                    for (int i = 0; i < domainWidth; i++) {
                        isInDomain[i + j * domainWidth] = 0;
                    }
                } 
            }
        }

        // Writing to buffers
        for (int i = 0; i < 2; i++) {
            uBuffers[i].SetData(u);
            vBuffers[i].SetData(v);
        }

        domainBuffer.SetData(isInDomain);
        pBuffer.SetData(p);

        // Storing scenario id
        currentScenario = scenarioId;

        total_deltaTime = 0f;
    }

    void init() {
        // Creating buffers
        uBuffers = new ComputeBuffer[2];
        vBuffers = new ComputeBuffer[2];

        for (int i = 0; i < 2; i++) {
            uBuffers[i] = new ComputeBuffer(domainWidth * domainHeight, sizeof(float));
            vBuffers[i] = new ComputeBuffer(domainWidth * domainHeight, sizeof(float));
        }

        domainBuffer = new ComputeBuffer(domainWidth * domainHeight, sizeof(int));
        pBuffer = new ComputeBuffer(domainWidth * domainHeight, sizeof(float));

        // Loading scenario
        loadScenario(1);

        // Kernels
        momentumKernelId = solveMomentumShader.FindKernel("CSMain");
        incompressibilityKernelId = solveIncompressiblityShader.FindKernel("CSMain");
        dampPressureKernelId = solveIncompressiblityShader.FindKernel("DampPressure");
        dragKernelId = dragShader.FindKernel("CSMain");
        renderKernelId = renderShader.FindKernel("CSMain");
        streamlinesKernelId = streamlinesShader.FindKernel("CSMain");

        // Shader uniforms
        solveMomentumShader.SetInt("domainWidth",domainWidth);
        solveMomentumShader.SetInt("domainHeight",domainHeight);
        solveMomentumShader.SetFloat("h",h);

        solveIncompressiblityShader.SetInt("domainWidth",domainWidth);
        solveIncompressiblityShader.SetInt("domainHeight",domainHeight);
        solveIncompressiblityShader.SetFloat("h",h);

        dragShader.SetInt("domainWidth",domainWidth);
        dragShader.SetInt("domainHeight",domainHeight);

        domainShader.SetInt("domainWidth",domainWidth);
        domainShader.SetInt("domainHeight",domainHeight);

        renderShader.SetInt("domainWidth",domainWidth);
        renderShader.SetInt("domainHeight",domainHeight);

        streamlinesShader.SetInt("domainWidth",domainWidth);
        streamlinesShader.SetInt("domainHeight",domainHeight);

        // Domain buffer set
        renderShader.SetBuffer(renderKernelId,"domain",domainBuffer);
        solveMomentumShader.SetBuffer(momentumKernelId,"domain",domainBuffer);
        dragShader.SetBuffer(dragKernelId,"domain",domainBuffer);
        domainShader.SetBuffer(domainKernelId,"domain",domainBuffer);
        solveIncompressiblityShader.SetBuffer(incompressibilityKernelId,"domain",domainBuffer);

        // Pressure buffer
        renderShader.SetBuffer(renderKernelId,"pressure",pBuffer);
        solveMomentumShader.SetBuffer(momentumKernelId,"p",pBuffer);
        solveIncompressiblityShader.SetBuffer(incompressibilityKernelId,"pressure",pBuffer);
        solveIncompressiblityShader.SetBuffer(dampPressureKernelId,"pressure",pBuffer);

        // Render Texture
        textureOut = new RenderTexture(domainWidth,domainHeight,0);
        textureOut.enableRandomWrite = true;
        textureOut.Create();

        renderMaterial.mainTexture = textureOut;
    }

    void Start() {
        init();
    }

    void printDebugData() {
        Vector3 mouseWorldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector2 localPos = transform.InverseTransformPoint(mouseWorldPos);

        Vector2 mouseDomainPos = (localPos + 0.5f*Vector2.one) * new Vector2(domainWidth,domainHeight);

        if (lastMousePos.x >= 0 && lastMousePos.y >= 0 && lastMousePos.x <= domainWidth && lastMousePos.y <= domainHeight) {
            int[] domain = new int[domainWidth*domainHeight];
            float[] u = new float[domainWidth*domainHeight];
            float[] v = new float[domainWidth*domainHeight];
            float[] p = new float[domainWidth*domainHeight];

            domainBuffer.GetData(domain);
            uBuffers[currentBufferId].GetData(u);
            vBuffers[currentBufferId].GetData(v);
            pBuffer.GetData(p);

            Vector2Int cellPos = new Vector2Int((int)MathF.Floor(mouseDomainPos.x), (int)MathF.Floor(mouseDomainPos.y));
            int index = cellPos.x + cellPos.y * domainWidth;

            Debug.Log("Info for cell " + cellPos + " | Domain: " + domain[index] + " | u: (" + u[index] + ", " + v[index] + ") | p: " + p[index]);
        }

        lastMousePos = mouseDomainPos;
    }

    float total_deltaTime = 0f;
    private float dt_per_iter = 0.001f;

    void HandleUserInput() {
        if (Input.GetKeyDown(KeyCode.Comma)) { // Step sim once
            total_deltaTime += dt_per_iter;
            stepSimulation(dt_per_iter);
            Debug.Log("Stepping scenario to " + Mathf.RoundToInt(1000*total_deltaTime) + " ms");
        }

        if (Input.GetKeyDown(KeyCode.R)) { // Reload current scenario
            loadScenario(currentScenario);
        }

        if (Input.GetKeyDown(KeyCode.Space)) { // Pause/play sim
            autoPlay = !autoPlay;
        }

        if (Input.GetKeyDown(KeyCode.A)) { // Toggling streamlines
            streamLinesEnabled = !streamLinesEnabled;
        }

        if (Input.GetKeyDown(KeyCode.S)) { // Taking screenshot
            screenshot();
        }

        if (Input.GetKeyDown(KeyCode.D)) { // Saving + Loading domains
            if (Input.GetKey(KeyCode.LeftShift)) {
                loadDomainFromFile("testDomain.json");
            } else {
                saveDomain();
            }
        }

        if (Input.GetKeyDown(KeyCode.Q)) {
            printDebugData();
        }

        if (Input.GetKeyDown(KeyCode.F)) {
            fixDomain();
        }

        // Number keys
        for (int i = 0; i <= 9; i++) {
            KeyCode key = KeyCode.Alpha0 + i;
            if (Input.GetKeyDown(key)) {
                int numberPressed = key - KeyCode.Alpha0;

                if (Input.GetKey(KeyCode.LeftShift)) {
                    loadScenario(numberPressed);
                } else {
                    setRenderMode(numberPressed);
                }
            }
        }

        // if (Input.GetMouseButtonDown(0)) { // Poll velocity field
        //     Vector3 mouseWorldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        //     Vector2 localPos = transform.InverseTransformPoint(mouseWorldPos);

        //     Vector2 domain_pos = (localPos + 0.5f*Vector2.one) * new Vector2(domainWidth,domainHeight);
        //     Vector2Int cell_pos = new Vector2Int(Mathf.FloorToInt(domain_pos.x),Mathf.FloorToInt(domain_pos.y));

        //     if (cell_pos.x >= 0 && cell_pos.x < domainWidth && cell_pos.y >= 0 && cell_pos.y < domainHeight) { // Ensuring not out of bounds
        //         Debug.Log("Velocity at cell (" + cell_pos + ") = (" + u[cell_pos.x,cell_pos.y] + "," + v[cell_pos.x,cell_pos.y] + ")");
        //     }
        // }
    }

    void Update() {
        HandleUserInput();
        render();

        if (streamLinesEnabled) {
            drawStreamLines();
        }
    }

    void FixedUpdate() {
        float dt = Time.fixedDeltaTime;

        bool isSimRunning = autoPlay || Input.GetKey(KeyCode.Period);

        handleUserDragging(dt, isSimRunning);

        if (isSimRunning) { // Stepping the simulation continuously
            total_deltaTime += dt;

            for (int iter = 0; iter < simulationIterationsPerFrame; iter++) {
                stepSimulation(dt/simulationIterationsPerFrame);
            }
        }
    }

    void OnDestroy() {
        textureOut.Release();

        for (int i = 0; i < 2; i++) {
            uBuffers[i].Release();
            vBuffers[i].Release();
        }

        domainBuffer.Release();
    }
}