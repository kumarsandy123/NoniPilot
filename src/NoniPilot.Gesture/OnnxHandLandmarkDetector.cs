using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NoniPilot.Gesture;

/// <summary>
/// Runs a MediaPipe hand-landmark model (converted to ONNX) via ONNX Runtime. Reads the
/// model's actual input name/shape at load time rather than hardcoding it, since different
/// community ONNX exports of the same MediaPipe model name tensors differently - only the
/// underlying MediaPipe output *contract* is assumed: a 63-value (21 landmarks x,y,z) output
/// in the model's input-pixel coordinate space, plus a scalar hand-presence score.
/// </summary>
public sealed class OnnxHandLandmarkDetector : IHandLandmarkDetector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly bool _channelsFirst;

    public OnnxHandLandmarkDetector(string modelPath)
    {
        // Measured live (2026-09-13): with ONNX Runtime's default SessionOptions, this process
        // consumed ~750% CPU (7.5 of 12 logical cores) continuously the entire time the camera
        // was on, dropping to ~0.4% the instant the camera was stopped - i.e. this inference
        // session, not the camera capture or the rest of the gesture pipeline, was the actual
        // cost. Default ORT auto-sizes its intra/inter-op thread pools to the machine's core
        // count AND has those worker threads actively spin-wait (not sleep) between inference
        // calls to minimize latency jitter - exactly the kind of always-on background CPU burn
        // that doesn't show up as "doing work" but shows up on every core in Task Manager. This
        // model is small (224x224 input) and only runs a few times a second at most (throttled
        // in WebcamGestureEngine) - it has no need for multi-threaded parallelism within a single
        // inference, and explicitly disabling spin-waiting lets idle worker threads actually
        // sleep between calls instead of polling.
        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        sessionOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        sessionOptions.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

        _session = new InferenceSession(modelPath, sessionOptions);

        var inputMeta = _session.InputMetadata.First();
        _inputName = inputMeta.Key;

        var dims = inputMeta.Value.Dimensions;
        _channelsFirst = dims.Length == 4 && dims[1] == 3;
        var declaredSize = _channelsFirst ? dims[3] : dims[2];
        _inputSize = declaredSize > 0 ? declaredSize : 224; // dynamic axis - fall back to MediaPipe's usual 224
    }

    public HandDetectionResult Detect(Mat croppedRoiBgr)
    {
        using var resized = new Mat();
        Cv2.Resize(croppedRoiBgr, resized, new Size(_inputSize, _inputSize));
        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = _channelsFirst ? ToChwTensor(rgb, _inputSize) : ToHwcTensor(rgb, _inputSize);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

        using var results = _session.Run(inputs);

        // This model (MediaPipe hand_landmark, "full" variant, 1x3x224x224 NCHW) declares four
        // outputs in this order: screen_landmarks[1,63], presence[1,1], handedness[1,1],
        // world_landmarks[1,63]. Two outputs share each shape, so only the FIRST match for
        // each shape is taken - the second 63-length output (world landmarks, metric-scale)
        // and second 1-length output (handedness) are deliberately ignored here.
        float[]? landmarkValues = null;
        float? presence = null;

        foreach (var output in results)
        {
            var values = output.AsEnumerable<float>().ToArray();
            if (values.Length == 63 && landmarkValues is null)
            {
                landmarkValues = values;
            }
            else if (values.Length == 1 && presence is null)
            {
                presence = values[0];
            }
        }

        presence ??= 1f;

        if (landmarkValues is null)
        {
            return new HandDetectionResult { HandDetected = false };
        }

        var landmarks = new List<HandLandmark>(21);
        for (var i = 0; i < 21; i++)
        {
            landmarks.Add(new HandLandmark(
                landmarkValues[i * 3] / _inputSize,
                landmarkValues[i * 3 + 1] / _inputSize,
                landmarkValues[i * 3 + 2] / _inputSize));
        }

        return new HandDetectionResult
        {
            HandDetected = presence.Value > 0.5f,
            Landmarks = landmarks,
            Confidence = presence.Value,
        };
    }

    private static DenseTensor<float> ToChwTensor(Mat rgb, int size)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = pixel.Item0 / 255f;
                tensor[0, 1, y, x] = pixel.Item1 / 255f;
                tensor[0, 2, y, x] = pixel.Item2 / 255f;
            }
        }
        return tensor;
    }

    private static DenseTensor<float> ToHwcTensor(Mat rgb, int size)
    {
        var tensor = new DenseTensor<float>(new[] { 1, size, size, 3 });
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, y, x, 0] = pixel.Item0 / 255f;
                tensor[0, y, x, 1] = pixel.Item1 / 255f;
                tensor[0, y, x, 2] = pixel.Item2 / 255f;
            }
        }
        return tensor;
    }

    public void Dispose() => _session.Dispose();
}
