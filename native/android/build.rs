// GTA IV's XMA sounds: FFmpeg's decoder (ci/ffmpeg-android.sh, its folder in DELIRIUM_XMA_DIR) linked in when
// it's there; else a stub that decodes nothing (a build without it still works, silent).
fn main() {
    println!("cargo::rustc-check-cfg=cfg(xma_ffmpeg)");
    println!("cargo:rerun-if-env-changed=DELIRIUM_XMA_DIR");
    let Ok(dir) = std::env::var("DELIRIUM_XMA_DIR") else { return };
    if !std::path::Path::new(&dir).join("libxma.a").exists() {
        println!("cargo:warning=no libxma.a in {dir}: no XMA sounds");
        return;
    }
    println!("cargo:rustc-cfg=xma_ffmpeg");
    println!("cargo:rustc-link-search=native={dir}");
    println!("cargo:rustc-link-search=native={dir}/lib");
    println!("cargo:rustc-link-lib=static=xma");
    println!("cargo:rustc-link-lib=static=avcodec");
    println!("cargo:rustc-link-lib=static=avutil");
    println!("cargo:rustc-link-lib=m");
}
