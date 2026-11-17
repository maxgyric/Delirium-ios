//! libdelirium_ios.a: Delirium's C API (nm_*, from delirium-sdk) and delirium_anim's (da_*) in one static library
//! for the iPhone build. Unity calls them through [DllImport("__Internal")].
pub use delirium_anim;
pub use delirium_bike;
pub use delirium_sound;
pub use delirium_bsp;
pub use delirium_physics;

/// Keeps both libraries' exported functions in the archive whatever the linker decides is used.
#[unsafe(no_mangle)]
pub extern "C" fn delirium_ios_version() -> i32 {
    1
}
