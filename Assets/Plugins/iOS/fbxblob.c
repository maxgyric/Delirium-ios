// FBX for Delirium Sandbox's VRChat avatar import (Vrc/FbxBlob.cs reads what this writes): ufbx loads the file in
// glTF's frame (right-handed, Y up, metres); each mesh comes out as one blob: its vertices where they sit in the
// bind pose (world), skin (4 bones a vertex), bones (names, inverse bind matrices), triangles per material slot,
// blend shapes (world-space offsets). All little-endian.
#include "ufbx.h"
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>

typedef struct { uint8_t* p; size_t n, cap; } Buf;
static void put(Buf* b, const void* d, size_t n)
{
    if (b->n + n > b->cap)
    {
        size_t c = b->cap ? b->cap * 2 : 1 << 20;
        while (c < b->n + n) c *= 2;
        b->p = (uint8_t*)realloc(b->p, c);
        b->cap = c;
    }
    memcpy(b->p + b->n, d, n);
    b->n += n;
}
static void u32(Buf* b, uint32_t v) { put(b, &v, 4); }
static void f32(Buf* b, float v) { put(b, &v, 4); }
static void str(Buf* b, ufbx_string s) { u32(b, (uint32_t)s.length); put(b, s.data, s.length); }
static void mat(Buf* b, ufbx_matrix m) // column-major 4x4
{
    for (int c = 0; c < 4; ++c)
    {
        for (int r = 0; r < 3; ++r) f32(b, (float)m.cols[c].v[r]);
        f32(b, c == 3 ? 1.f : 0.f);
    }
}

typedef struct { ufbx_vec3 pos, nrm; ufbx_vec2 uv; float w[4]; uint32_t j[4]; uint32_t vtx; } Vert;

static void mesh_blob(Buf* b, ufbx_node* node, ufbx_mesh* mesh, uint32_t node_index)
{
    ufbx_skin_deformer* skin = mesh->skin_deformers.count ? mesh->skin_deformers.data[0] : NULL;
    ufbx_matrix toWorld = node->geometry_to_world;
    if (skin && skin->clusters.count)
    {
        ufbx_skin_cluster* c = skin->clusters.data[0];
        toWorld = ufbx_matrix_mul(&c->bind_to_world, &c->geometry_to_bone);
    }
    ufbx_matrix toWorldN = ufbx_matrix_for_normals(&toWorld);
    size_t maxTri = mesh->max_face_triangles * 3;
    uint32_t* tri = (uint32_t*)malloc(sizeof(uint32_t) * (maxTri ? maxTri : 3));
    size_t corners = mesh->num_triangles * 3;
    Vert* v = (Vert*)calloc(corners ? corners : 1, sizeof(Vert));
    uint32_t* slot = (uint32_t*)malloc(sizeof(uint32_t) * (mesh->num_triangles ? mesh->num_triangles : 1));
    size_t k = 0, t = 0;
    for (size_t f = 0; f < mesh->num_faces; ++f)
    {
        ufbx_face face = mesh->faces.data[f];
        uint32_t n = ufbx_triangulate_face(tri, maxTri, mesh, face);
        uint32_t m = mesh->face_material.count ? mesh->face_material.data[f] : 0;
        for (uint32_t i = 0; i < n * 3; ++i)
        {
            uint32_t ix = tri[i];
            Vert* o = &v[k++];
            uint32_t vi = mesh->vertex_indices.data[ix];
            o->vtx = vi;
            o->pos = ufbx_transform_position(&toWorld, mesh->vertices.data[vi]);
            if (mesh->vertex_normal.exists)
            {
                ufbx_vec3 nn = ufbx_transform_direction(&toWorldN, ufbx_get_vertex_vec3(&mesh->vertex_normal, ix));
                double l = sqrt(nn.x * nn.x + nn.y * nn.y + nn.z * nn.z);
                if (l > 0) nn.x /= l, nn.y /= l, nn.z /= l;
                o->nrm = nn;
            }
            if (mesh->vertex_uv.exists) o->uv = ufbx_get_vertex_vec2(&mesh->vertex_uv, ix);
            if (skin && vi < skin->vertices.count)
            {
                ufbx_skin_vertex sv = skin->vertices.data[vi];
                // the 4 strongest, normalised
                for (uint32_t w = 0; w < sv.num_weights; ++w)
                {
                    ufbx_skin_weight sw = skin->weights.data[sv.weight_begin + w];
                    int slotw = -1;
                    for (int q = 0; q < 4; ++q)
                        if (slotw < 0 || o->w[q] < o->w[slotw]) slotw = q;
                    if ((float)sw.weight > o->w[slotw]) o->w[slotw] = (float)sw.weight, o->j[slotw] = sw.cluster_index;
                }
                float sum = o->w[0] + o->w[1] + o->w[2] + o->w[3];
                if (sum > 0)
                    for (int q = 0; q < 4; ++q) o->w[q] /= sum;
            }
        }
        for (uint32_t i = 0; i < n; ++i) slot[t++] = m;
    }
    // shared vertices: the same corner everywhere it repeats
    ufbx_vertex_stream stream = {v, k, sizeof(Vert)};
    uint32_t* idx = (uint32_t*)malloc(sizeof(uint32_t) * (k ? k : 1));
    ufbx_error err;
    size_t nv = k ? ufbx_generate_indices(&stream, 1, idx, k, NULL, &err) : 0;

    str(b, node->name);
    u32(b, node_index);
    u32(b, (uint32_t)nv);
    for (size_t i = 0; i < nv; ++i) f32(b, (float)v[i].pos.x), f32(b, (float)v[i].pos.y), f32(b, (float)v[i].pos.z);
    for (size_t i = 0; i < nv; ++i) f32(b, (float)v[i].nrm.x), f32(b, (float)v[i].nrm.y), f32(b, (float)v[i].nrm.z);
    for (size_t i = 0; i < nv; ++i) f32(b, (float)v[i].uv.x), f32(b, (float)v[i].uv.y);
    // bones (a mesh without a skin: one, its own node, flag 0)
    uint32_t nb = skin ? (uint32_t)skin->clusters.count : 0;
    u32(b, skin ? 1 : 0);
    u32(b, nb ? nb : 1);
    for (uint32_t c = 0; c < (nb ? nb : 1); ++c)
    {
        ufbx_skin_cluster* cl = nb ? skin->clusters.data[c] : NULL;
        str(b, cl ? (cl->bone_node ? cl->bone_node->name : cl->name) : node->name);
        ufbx_matrix bind = cl ? cl->bind_to_world : node->node_to_world;
        ufbx_matrix inv = ufbx_matrix_invert(&bind);
        mat(b, inv);
        mat(b, bind);
    }
    for (size_t i = 0; i < nv; ++i)
    {
        if (!nb) v[i].w[0] = 1.f, v[i].j[0] = 0;
        for (int q = 0; q < 4; ++q) u32(b, v[i].j[q]);
        for (int q = 0; q < 4; ++q) f32(b, v[i].w[q]);
    }
    // triangles per material slot
    uint32_t slots = (uint32_t)(mesh->materials.count ? mesh->materials.count : 1);
    u32(b, slots);
    for (uint32_t s = 0; s < slots; ++s)
    {
        ufbx_string nm = mesh->materials.count && mesh->materials.data[s] ? mesh->materials.data[s]->name : (ufbx_string){"", 0};
        str(b, nm);
        uint32_t cnt = 0;
        for (size_t q = 0; q < t; ++q) cnt += slot[q] == s;
        u32(b, cnt * 3);
        for (size_t q = 0; q < t; ++q)
            if (slot[q] == s) u32(b, idx[q * 3]), u32(b, idx[q * 3 + 1]), u32(b, idx[q * 3 + 2]);
    }
    // blend shapes (each channel's full shape), offsets in the world frame
    uint32_t nshapes = 0;
    for (size_t d = 0; d < mesh->blend_deformers.count; ++d)
        for (size_t c = 0; c < mesh->blend_deformers.data[d]->channels.count; ++c)
            if (mesh->blend_deformers.data[d]->channels.data[c]->target_shape) ++nshapes;
    u32(b, nshapes);
    for (size_t d = 0; d < mesh->blend_deformers.count; ++d)
        for (size_t c = 0; c < mesh->blend_deformers.data[d]->channels.count; ++c)
        {
            ufbx_blend_channel* ch = mesh->blend_deformers.data[d]->channels.data[c];
            ufbx_blend_shape* sh = ch->target_shape;
            if (!sh) continue;
            str(b, ch->name);
            for (size_t i = 0; i < nv; ++i)
            {
                ufbx_vec3 off = ufbx_get_blend_shape_vertex_offset(sh, v[i].vtx);
                ufbx_vec3 w = ufbx_transform_direction(&toWorld, off);
                f32(b, (float)w.x), f32(b, (float)w.y), f32(b, (float)w.z);
            }
        }
    free(idx), free(v), free(slot), free(tri);
}

// The whole file as one blob ("FXB1", meshes...); 0 on success, else an error text in `out` (free with fx_free).
int fx_load(const char* path, uint8_t** out, int* len)
{
    ufbx_load_opts opts = {0};
    opts.target_axes = ufbx_axes_right_handed_y_up;
    opts.target_unit_meters = 1.0f;
    opts.generate_missing_normals = true;
    // units and axes baked into the vertices and the nodes' translations, as Unity's import does
    opts.space_conversion = UFBX_SPACE_CONVERSION_MODIFY_GEOMETRY;
    ufbx_error error;
    ufbx_scene* scene = ufbx_load_file(path, &opts, &error);
    Buf b = {0};
    if (!scene)
    {
        char msg[512];
        ufbx_format_error(msg, sizeof msg, &error);
        put(&b, msg, strlen(msg) + 1);
        *out = b.p, *len = (int)b.n;
        return 1;
    }
    put(&b, "FXB4", 4);
    uint32_t count = 0;
    for (size_t i = 0; i < scene->nodes.count; ++i) count += scene->nodes.data[i]->mesh != NULL;
    u32(&b, count);
    for (size_t i = 0; i < scene->nodes.count; ++i)
    {
        ufbx_node* n = scene->nodes.data[i];
        if (n->mesh) mesh_blob(&b, n, n->mesh, (uint32_t)i);
    }
    // every node's bind/rest world matrix by name (to fit the file to the prefab's skeleton)
    u32(&b, (uint32_t)scene->nodes.count);
    for (size_t i = 0; i < scene->nodes.count; ++i)
    {
        ufbx_node* n = scene->nodes.data[i];
        str(&b, n->name);
        u32(&b, n->parent ? n->parent->typed_id : 0xFFFFFFFFu);
        ufbx_transform t = n->local_transform;
        f32(&b, (float)t.translation.x), f32(&b, (float)t.translation.y), f32(&b, (float)t.translation.z);
        f32(&b, (float)t.rotation.x), f32(&b, (float)t.rotation.y), f32(&b, (float)t.rotation.z), f32(&b, (float)t.rotation.w);
        f32(&b, (float)t.scale.x), f32(&b, (float)t.scale.y), f32(&b, (float)t.scale.z);
        mat(&b, n->node_to_world);
    }
    ufbx_free_scene(scene);
    *out = b.p, *len = (int)b.n;
    return 0;
}

void fx_free(uint8_t* p) { free(p); }
