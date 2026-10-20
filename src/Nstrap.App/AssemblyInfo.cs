using System.Reflection;

// Reserve 65 UTF-16 words in the native FileDescription, including the compiler's
// terminating NUL. The apphost copies this resource before single-file bundling.
// Personalization can then replace only these bytes without moving the bundle.
[assembly: AssemblyTitle("Nstrap"
    + "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"
    + "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"
    + "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"
    + "\0\0\0\0\0\0\0\0\0\0")]
