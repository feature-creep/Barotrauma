﻿using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class Holdable : Pickable, IServerSerializable, IClientSerializable
    {
        const float MaxAttachDistance = 150.0f;

        //the position(s) in the item that the Character grabs
        protected Vector2[] handlePos;
        private readonly Vector2[] scaledHandlePos;

        private readonly InputType prevPickKey;
        private string prevMsg;
        private Dictionary<RelatedItem.RelationType, List<RelatedItem>> prevRequiredItems;

        //the distance from the holding characters elbow to center of the physics body of the item
        protected Vector2 holdPos;

        protected Vector2 aimPos;

        private float swingState;

        private Character prevEquipper;

        private bool attachable, attached, attachedByDefault;
        private Voronoi2.VoronoiCell attachTargetCell;
        private PhysicsBody body;
        public PhysicsBody Pusher
        {
            get;
            private set;
        }
        [Serialize(true, true, description: "Is the item currently able to push characters around? True by default. Only valid if blocksplayers is set to true.")]
        public bool CanPush
        {
            get;
            set;
        }

        //the angle in which the Character holds the item
        protected float holdAngle;

        public PhysicsBody Body
        {
            get { return item.body ?? body; }
        }

        [Serialize(false, true, description: "Is the item currently attached to a wall (only valid if Attachable is set to true).")]
        public bool Attached
        {
            get { return attached && item.ParentInventory == null; }
            set
            {
                attached = value;
                item.SetActiveSprite();
            }
        }

        [Serialize(true, true, description: "Can the item be pointed to a specific direction or do the characters always hold it in a static pose.")]
        public bool Aimable
        {
            get;
            set;
        }

        [Serialize(false, false, description: "Should the character adjust its pose when aiming with the item. Most noticeable underwater, where the character will rotate its entire body to face the direction the item is aimed at.")]
        public bool ControlPose
        {
            get;
            set;
        }

        [Serialize(false, false, description: "Use the hand rotation instead of torso rotation for the item hold angle. Enable this if you want the item just to follow with the arm when not aiming instead of forcing the arm to a hold pose.")]
        public bool UseHandRotationForHoldAngle
        {
            get;
            set;
        }

        [Serialize(false, false, description: "Can the item be attached to walls.")]
        public bool Attachable
        {
            get { return attachable; }
            set { attachable = value; }
        }

        [Serialize(true, false, description: "Can the item be reattached to walls after it has been deattached (only valid if Attachable is set to true).")]
        public bool Reattachable
        {
            get;
            set;
        }

        [Serialize(false, false, description: "Can the item only be attached in limited amount? Uses permanent stat values to check for legibility.")]
        public bool LimitedAttachable
        {
            get;
            set;
        }

        [Serialize(false, false, description: "Should the item be attached to a wall by default when it's placed in the submarine editor.")]
        public bool AttachedByDefault
        {
            get { return attachedByDefault; }
            set { attachedByDefault = value; }
        }

        [Editable, Serialize("0.0,0.0", false, description: "The position the character holds the item at (in pixels, as an offset from the character's shoulder)."+
            " For example, a value of 10,-100 would make the character hold the item 100 pixels below the shoulder and 10 pixels forwards.")]
        public Vector2 HoldPos
        {
            get { return ConvertUnits.ToDisplayUnits(holdPos); }
            set { holdPos = ConvertUnits.ToSimUnits(value); }
        }

        [Serialize("0.0,0.0", false, description: "The position the character holds the item at when aiming (in pixels, as an offset from the character's shoulder)."+
            " Works similarly as HoldPos, except that the position is rotated according to the direction the player is aiming at. For example, a value of 10,-100 would make the character hold the item 100 pixels below the shoulder and 10 pixels forwards when aiming directly to the right.")]
        public Vector2 AimPos
        {
            get { return ConvertUnits.ToDisplayUnits(aimPos); }
            set { aimPos = ConvertUnits.ToSimUnits(value); }
        }

        [Editable, Serialize(0.0f, false, description: "The rotation at which the character holds the item (in degrees, relative to the rotation of the character's hand).")]
        public float HoldAngle
        {
            get { return MathHelper.ToDegrees(holdAngle); }
            set { holdAngle = MathHelper.ToRadians(value); }
        }

        private Vector2 swingAmount;
        [Editable, Serialize("0.0,0.0", false, description: "How much the item swings around when aiming/holding it (in pixels, as an offset from AimPos/HoldPos).")]
        public Vector2 SwingAmount
        {
            get { return ConvertUnits.ToDisplayUnits(swingAmount); }
            set { swingAmount = ConvertUnits.ToSimUnits(value); }
        }
        
        [Editable, Serialize(0.0f, false, description: "How fast the item swings around when aiming/holding it (only valid if SwingAmount is set).")]
        public float SwingSpeed { get; set; }

        [Editable, Serialize(false, false, description: "Should the item swing around when it's being held.")]
        public bool SwingWhenHolding { get; set; }
        [Editable, Serialize(false, false, description: "Should the item swing around when it's being aimed.")]
        public bool SwingWhenAiming { get; set; }
        [Editable, Serialize(false, false, description: "Should the item swing around when it's being used (for example, when firing a weapon or a welding tool).")]
        public bool SwingWhenUsing { get; set; }
        
        public Holdable(Item item, XElement element)
            : base(item, element)
        {
            body = item.body;

            Pusher = null;
            if (element.GetAttributeBool("blocksplayers", false))
            {
                Pusher = new PhysicsBody(item.body.width, item.body.height, item.body.radius, item.body.Density)
                {
                    BodyType = BodyType.Dynamic,
                    CollidesWith = Physics.CollisionCharacter,
                    CollisionCategories = Physics.CollisionItemBlocking,
                    Enabled = false,
                    UserData = "Holdable.Pusher"
                };
                Pusher.FarseerBody.OnCollision += OnPusherCollision;
                Pusher.FarseerBody.FixedRotation = false;
                Pusher.FarseerBody.IgnoreGravity = true;
            }

            handlePos = new Vector2[2];
            scaledHandlePos = new Vector2[2];
            Vector2 previousValue = Vector2.Zero;
            for (int i = 1; i < 3; i++)
            {
                int index = i - 1;
                string attributeName = "handle" + i;
                var attribute = element.Attribute(attributeName);
                // If no value is defind for handle2, use the value of handle1.
                var value = attribute != null ? ConvertUnits.ToSimUnits(XMLExtensions.ParseVector2(attribute.Value)) : previousValue;
                handlePos[index] = value;
                previousValue = value;
            }

            canBePicked = true;
            
            if (attachable)
            {
                prevMsg = DisplayMsg;
                prevPickKey = PickKey;
                prevRequiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>(requiredItems);
                                
                if (item.Submarine != null)
                {
                    if (item.Submarine.Loading)
                    {
                        AttachToWall();
                        Attached = false;
                    }
                    else //the submarine is not being loaded, which means we're either in the sub editor or the item has been spawned mid-round
                    {
                        if (Screen.Selected == GameMain.SubEditorScreen)
                        {
                            //in the sub editor, attach
                            AttachToWall();
                        }
                        else
                        {
                            //spawned mid-round, deattach
                            DeattachFromWall();
                        }
                    }
                }
            }
            characterUsable = element.GetAttributeBool("characterusable", true);
        }

        private bool OnPusherCollision(Fixture sender, Fixture other, Contact contact)
        {
            if (other.Body.UserData is Character character)
            {
                if (!IsActive) { return false; }
                if (!CanPush) { return false; }
                return character != picker;
            }
            else
            {
                return true;
            }
        }

        public override void Load(XElement componentElement, bool usePrefabValues, IdRemap idRemap)
        {
            base.Load(componentElement, usePrefabValues, idRemap);

            if (usePrefabValues)
            {
                //this needs to be loaded regardless
                Attached = componentElement.GetAttributeBool("attached", attached);
            }

            if (attachable)
            {
                prevMsg = DisplayMsg;
                prevRequiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>(requiredItems);
            }
        }

        public override void Drop(Character dropper)
        {
            Drop(true, dropper);
        }

        private void Drop(bool dropConnectedWires, Character dropper)
        {
            GetRope()?.Snap();
            if (dropConnectedWires)
            {
                DropConnectedWires(dropper);
            }

            if (attachable)
            {
                DeattachFromWall();

                if (body != null)
                {
                    item.body = body;
                }
            }

            if (Pusher != null) { Pusher.Enabled = false; }
            if (item.body != null) { item.body.Enabled = true; }
            IsActive = false;
            attachTargetCell = null;

            if (picker == null || picker.Removed)
            {
                if (dropper == null || dropper.Removed) { return; }
                picker = dropper;
            }
            if (picker.Inventory == null) { return; }

            item.Submarine = picker.Submarine;

            if (item.body != null)
            {
                if (item.body.Removed)
                {
                    DebugConsole.ThrowError(
                        "Failed to drop the Holdable component of the item \"" + item.Name + "\" (body has been removed"
                        + (item.Removed ? ", item has been removed)" : ")"));
                }
                else
                {
                    item.body.ResetDynamics();
                    Limb heldHand, arm;
                    if (picker.Inventory.IsInLimbSlot(item, InvSlotType.LeftHand))
                    {
                        heldHand = picker.AnimController.GetLimb(LimbType.LeftHand);
                        arm = picker.AnimController.GetLimb(LimbType.LeftArm);
                    }
                    else
                    {
                        heldHand = picker.AnimController.GetLimb(LimbType.RightHand);
                        arm = picker.AnimController.GetLimb(LimbType.RightArm);
                    }
                    if (heldHand != null && !heldHand.Removed && arm != null && !arm.Removed)
                    {
                        //hand simPosition is actually in the wrist so need to move the item out from it slightly
                        Vector2 diff = new Vector2(
                            (heldHand.SimPosition.X - arm.SimPosition.X) / 2f,
                            (heldHand.SimPosition.Y - arm.SimPosition.Y) / 2.5f);
                        item.SetTransform(heldHand.SimPosition + diff, 0.0f);
                    }
                    else
                    {
                        item.SetTransform(picker.SimPosition, 0.0f);
                    }     
                }           
            }

            picker.Inventory.RemoveItem(item);
            picker = null;
        }

        public override void Equip(Character character)
        {
            //if the item has multiple Pickable components (e.g. Holdable and Wearable, check that we don't equip it in hands when the item is worn or vice versa)
            if (item.GetComponents<Pickable>().Count() > 0)
            {
                bool inSuitableSlot = false;
                for (int i = 0; i < character.Inventory.Capacity; i++)
                {
                    if (character.Inventory.GetItemsAt(i).Contains(item))
                    {
                        if (character.Inventory.SlotTypes[i] != InvSlotType.Any && 
                            allowedSlots.Any(a => a.HasFlag(character.Inventory.SlotTypes[i])))
                        {
                            inSuitableSlot = true;
                            break;
                        }
                    }
                }
                if (!inSuitableSlot) { return; }
            }

            picker = character;

            if (item.Removed)
            {
                DebugConsole.ThrowError($"Attempted to equip a removed item ({item.Name})\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }

            var wearable = item.GetComponent<Wearable>();
            if (wearable != null)
            {
                //cannot hold and wear an item at the same time
                wearable.Unequip(character);
            }

            if (character != null) { item.Submarine = character.Submarine; }
            if (item.body == null)
            {
                if (body != null)
                {
                    item.body = body;
                }
                else
                {
                    return;
                }
            }

            if (!item.body.Enabled)
            {
                Limb hand = picker.AnimController.GetLimb(LimbType.RightHand) ?? picker.AnimController.GetLimb(LimbType.LeftHand);
                item.SetTransform(hand != null ? hand.SimPosition : character.SimPosition, 0.0f);
            }

            bool alreadyEquipped = character.HasEquippedItem(item);
            if (picker.HasEquippedItem(item))
            {
                item.body.Enabled = true;
                item.body.PhysEnabled = false;
                IsActive = true;

#if SERVER
                if (picker != prevEquipper) { GameServer.Log(GameServer.CharacterLogName(character) + " equipped " + item.Name, ServerLog.MessageType.ItemInteraction); }
#endif
                prevEquipper = picker;
            }
            else
            {
                prevEquipper = null;
            }
        }

        public override void Unequip(Character character)
        {
#if SERVER
            if (prevEquipper != null)
            {
                GameServer.Log(GameServer.CharacterLogName(character) + " unequipped " + item.Name, ServerLog.MessageType.ItemInteraction);
            }
#endif
            prevEquipper = null;
            if (picker == null) { return; }
            item.body.PhysEnabled = true;
            item.body.Enabled = false;
            IsActive = false;
        }

        public bool CanBeAttached(Character user)
        {
            if (!attachable || !Reattachable) { return false; }

            //can be attached anywhere in sub editor
            if (Screen.Selected == GameMain.SubEditorScreen) { return true; }

            Vector2 attachPos = user == null ? item.WorldPosition : GetAttachPosition(user, useWorldCoordinates: true);

            //can be attached anywhere inside hulls
            if (item.CurrentHull != null && Submarine.RectContains(item.CurrentHull.WorldRect, attachPos)) { return true; }

            return Structure.GetAttachTarget(attachPos) != null || GetAttachTargetCell(100.0f) != null;
        }

        public bool CanBeDeattached()
        {
            if (!attachable || !attached) { return true; }

            //allow deattaching everywhere in sub editor
            if (Screen.Selected == GameMain.SubEditorScreen) { return true; }

            if (item.GetComponent<LevelResource>() != null) { return true; }

            if (item.GetComponent<Planter>() is { } planter && planter.GrowableSeeds.Any(seed => seed != null)) { return false; } 

            //if the item has a connection panel and rewiring is disabled, don't allow deattaching
            var connectionPanel = item.GetComponent<ConnectionPanel>();
            if (connectionPanel != null && !connectionPanel.AlwaysAllowRewiring && (connectionPanel.Locked || !(GameMain.NetworkMember?.ServerSettings?.AllowRewiring ?? true)))
            {
                return false;
            }

            if (item.CurrentHull == null)
            {
                return attachTargetCell != null || Structure.GetAttachTarget(item.WorldPosition) != null;
            }
            else
            {
                return true;
            }
        }

        public override bool Pick(Character picker)
        {
            if (item.Removed)
            {
                DebugConsole.ThrowError($"Attempted to pick up a removed item ({item.Name})\n" + Environment.StackTrace.CleanupStackTrace());
                return false;
            }

            if (!attachable)
            {
                return base.Pick(picker);
            }

            if (!CanBeDeattached()) { return false; }

            if (Attached)
            {
                return base.Pick(picker);
            }
            else
            {

                //not attached -> pick the item instantly, ignoring picking time
                return OnPicked(picker);
            }
        }

        public override bool OnPicked(Character picker)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient)
            {
                return false;
            }
            if (base.OnPicked(picker))
            {
                DeattachFromWall();

#if SERVER
                if (GameMain.Server != null && attachable)
                {
                    item.CreateServerEvent(this);
                    if (picker != null)
                    {
                        GameServer.Log(GameServer.CharacterLogName(picker) + " detached " + item.Name + " from a wall", ServerLog.MessageType.ItemInteraction);
                    }
                }
#endif
                return true;
            }

            return false;
        }

        public void AttachToWall()
        {
            if (!attachable) { return; }

            //outside hulls/subs -> we need to check if the item is being attached on a structure outside the sub
            if (item.CurrentHull == null && item.Submarine == null)
            {
                Structure attachTarget = Structure.GetAttachTarget(item.WorldPosition);
                if (attachTarget != null)
                {
                    if (attachTarget.Submarine != null)
                    {
                        //set to submarine-relative position
                        item.SetTransform(ConvertUnits.ToSimUnits(item.WorldPosition - attachTarget.Submarine.Position), 0.0f, false);
                    }
                    item.Submarine = attachTarget.Submarine;
                }
                else
                {
                    attachTargetCell = GetAttachTargetCell(150.0f);
                    if (attachTargetCell != null) { IsActive = true; }
                }
            }

            var containedItems = item.OwnInventory?.AllItems;
            if (containedItems != null)
            {
                foreach (Item contained in containedItems)
                {
                    if (contained?.body == null) { continue; }
                    contained.SetTransform(item.SimPosition, contained.body.Rotation);
                }
            }

            body.Enabled = false;
            item.body = null;

            DisplayMsg = prevMsg;
            PickKey = prevPickKey;
            requiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>(prevRequiredItems);

            Attached = true;
        }

        public void DeattachFromWall()
        {
            if (!attachable) return;

            Attached = false;
            attachTargetCell = null;

            //make the item pickable with the default pick key and with no specific tools/items when it's deattached
            requiredItems.Clear();
            DisplayMsg = "";
            PickKey = InputType.Select;
        }

        public override void ParseMsg()
        {
            base.ParseMsg();
            if (Attachable)
            {
                prevMsg = DisplayMsg;
            }
        }

        public override bool Use(float deltaTime, Character character = null)
        {
            if (!attachable || item.body == null) { return character == null || (character.IsKeyDown(InputType.Aim) && characterUsable); }
            if (character != null)
            {
                if (!characterUsable && !attachable) { return false; }
                if (!character.IsKeyDown(InputType.Aim)) { return false; }
                if (!CanBeAttached(character)) { return false; }

                if (LimitedAttachable)
                {
                    if (character?.Info == null) 
                    {
                        DebugConsole.AddWarning("Character without CharacterInfo attempting to attach a limited attachable item!");
                        return false; 
                    }
                    Vector2 attachPos = GetAttachPosition(character, useWorldCoordinates: true);
                    Structure attachTarget = Structure.GetAttachTarget(attachPos);

                    int maxAttachableCount = (int)character.Info.GetSavedStatValue(StatTypes.MaxAttachableCount, item.Prefab.Identifier);
                    int currentlyAttachedCount = Item.ItemList.Count(
                        i => i.Submarine == attachTarget?.Submarine && i.GetComponent<Holdable>() is Holdable holdable && holdable.Attached && i.Prefab.Identifier == item.prefab.Identifier);
                    if (currentlyAttachedCount >= maxAttachableCount) 
                    {
#if CLIENT
                        GUI.AddMessage($"{TextManager.Get("itemmsgtotalnumberlimited")} ({currentlyAttachedCount}/{maxAttachableCount})", Color.Red);
#endif
                        return false; 
                    }
                }

                if (GameMain.NetworkMember != null)
                {
                    if (character != Character.Controlled)
                    {
                        return false;
                    }
                    else if (GameMain.NetworkMember.IsServer)
                    {
                        return false;
                    }
                    else
                    {
#if CLIENT
                        Vector2 attachPos = ConvertUnits.ToSimUnits(GetAttachPosition(character));
                        GameMain.Client.CreateEntityEvent(item, new object[] 
                        { 
                            NetEntityEvent.Type.ComponentState, 
                            item.GetComponentIndex(this), 
                            attachPos
                        });
#endif
                    }
                    return false;
                }
                else
                {
                    item.Drop(character);
                    item.SetTransform(ConvertUnits.ToSimUnits(GetAttachPosition(character)), 0.0f, findNewHull: false);
                }
            }

            AttachToWall();           

            return true;
        }

        public override bool SecondaryUse(float deltaTime, Character character = null)
        {
            return true;
        }

        private Vector2 GetAttachPosition(Character user, bool useWorldCoordinates = false)
        {
            if (user == null) { return useWorldCoordinates ? item.WorldPosition : item.Position; }

            Vector2 mouseDiff = user.CursorWorldPosition - user.WorldPosition;
            mouseDiff = mouseDiff.ClampLength(MaxAttachDistance);

            Vector2 userPos = useWorldCoordinates ? user.WorldPosition : user.Position;

            Vector2 attachPos = userPos + mouseDiff;

            if (user.Submarine == null && Level.Loaded != null)
            {
                bool edgeFound = false;
                foreach (var cell in Level.Loaded.GetCells(attachPos))
                {
                    if (cell.CellType != Voronoi2.CellType.Solid) { continue; }
                    foreach (var edge in cell.Edges)
                    {
                        if (!edge.IsSolid) { continue; }
                        if (MathUtils.GetLineIntersection(edge.Point1, edge.Point2, user.WorldPosition, attachPos, out Vector2 intersection))
                        {
                            attachPos = intersection;
                            edgeFound = true;
                            break;
                        }
                    }
                    if (edgeFound) { break; }
                }
            }

            return
                new Vector2(
                    MathUtils.RoundTowardsClosest(attachPos.X, Submarine.GridSize.X),
                    MathUtils.RoundTowardsClosest(attachPos.Y, Submarine.GridSize.Y));
        }

        private Voronoi2.VoronoiCell GetAttachTargetCell(float maxDist)
        {
            if (Level.Loaded == null) { return null; }
            foreach (var cell in Level.Loaded.GetCells(item.WorldPosition, searchDepth: 1))
            {
                if (cell.CellType != Voronoi2.CellType.Solid) { continue; }
                Vector2 diff = cell.Center - item.WorldPosition;
                if (diff.LengthSquared() > 0.0001f) { diff = Vector2.Normalize(diff); }
                if (cell.IsPointInside(item.WorldPosition + diff * maxDist))
                {
                    return cell;
                }
            }
            return null;
        }

        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            Update(deltaTime, cam);
        }

        public Rope GetRope()
        {
            var rangedWeapon = Item.GetComponent<RangedWeapon>();
            if (rangedWeapon != null)
            {
                var lastProjectile = rangedWeapon.LastProjectile;
                if (lastProjectile != null)
                {
                    return lastProjectile.Item.GetComponent<Rope>();
                }
            }
            return null;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (attachTargetCell != null)
            {
                if (attachTargetCell.CellType != Voronoi2.CellType.Solid)
                {
                    Drop(dropConnectedWires: true, dropper: null);
                }
                return;
            }

            if (item.body == null || !item.body.Enabled) { return; }
            if (picker == null || !picker.HasEquippedItem(item))
            {
                if (Pusher != null) { Pusher.Enabled = false; }
                if (attachTargetCell == null) { IsActive = false; }
                return;
            }

            if (picker == Character.Controlled && picker.IsKeyDown(InputType.Aim) && CanBeAttached(picker))
            {
                Drawable = true;
            }

            Vector2 swing = Vector2.Zero;
            if (swingAmount != Vector2.Zero && !picker.IsUnconscious && picker.Stun <= 0.0f)
            {
                swingState += deltaTime;
                swingState %= 1.0f;
                if (SwingWhenHolding ||
                    (SwingWhenAiming && picker.IsKeyDown(InputType.Aim)) ||
                    (SwingWhenUsing && picker.IsKeyDown(InputType.Aim) && picker.IsKeyDown(InputType.Shoot)))
                {
                    swing = swingAmount * new Vector2(
                        PerlinNoise.GetPerlin(swingState * SwingSpeed * 0.1f, swingState * SwingSpeed * 0.1f) - 0.5f,
                        PerlinNoise.GetPerlin(swingState * SwingSpeed * 0.1f + 0.5f, swingState * SwingSpeed * 0.1f + 0.5f) - 0.5f);
                }
            }
            
            ApplyStatusEffects(ActionType.OnActive, deltaTime, picker);

            if (item.body.Dir != picker.AnimController.Dir) 
            {
                item.FlipX(relativeToSub: false);
            }

            item.Submarine = picker.Submarine;
            
            if (picker.HeldItems.Contains(item))
            {
                scaledHandlePos[0] = handlePos[0] * item.Scale;
                scaledHandlePos[1] = handlePos[1] * item.Scale;
                bool aim = picker.IsKeyDown(InputType.Aim) && aimPos != Vector2.Zero && picker.CanAim;
                picker.AnimController.HoldItem(deltaTime, item, scaledHandlePos, holdPos + swing, aimPos + swing, aim, holdAngle);
                if (!aim)
                {
                    var rope = GetRope();
                    if (rope != null && rope.SnapWhenNotAimed)
                    {
                        rope.Snap();
                    }
                }
            }
            else
            {
                GetRope()?.Snap();
                Limb equipLimb = null;
                if (picker.Inventory.IsInLimbSlot(item, InvSlotType.Headset) || picker.Inventory.IsInLimbSlot(item, InvSlotType.Head))
                {
                    equipLimb = picker.AnimController.GetLimb(LimbType.Head);
                }
                else if (picker.Inventory.IsInLimbSlot(item, InvSlotType.InnerClothes) || 
                    picker.Inventory.IsInLimbSlot(item, InvSlotType.OuterClothes))
                {
                    equipLimb = picker.AnimController.GetLimb(LimbType.Torso);
                }

                if (equipLimb != null)
                {
                    float itemAngle = (equipLimb.Rotation + holdAngle * picker.AnimController.Dir);

                    Matrix itemTransfrom = Matrix.CreateRotationZ(equipLimb.Rotation);
                    Vector2 transformedHandlePos = Vector2.Transform(handlePos[0] * item.Scale, itemTransfrom);

                    item.body.ResetDynamics();
                    item.SetTransform(equipLimb.SimPosition - transformedHandlePos, itemAngle);
                }
            }
        }

        public override void FlipX(bool relativeToSub)
        {
            handlePos[0].X = -handlePos[0].X;
            handlePos[1].X = -handlePos[1].X;
            if (item.body != null)
            {
                item.body.Dir = -item.body.Dir;
            }
        }

        public override void OnItemLoaded()
        {
            if (item.Submarine != null && item.Submarine.Loading) return;
            OnMapLoaded();
            item.SetActiveSprite();
        }

        public override void OnMapLoaded()
        {
            if (!attachable) return;
            
            if (Attached)
            {
                AttachToWall();
            }
            else
            {
                if (item.ParentInventory != null)
                {
                    if (body != null)
                    {
                        item.body = body;
                        body.Enabled = false;
                    }
                }
                DeattachFromWall();
            }
        }

        protected override void RemoveComponentSpecific()
        {
            base.RemoveComponentSpecific();
            attachTargetCell = null;
            if (Pusher != null)
            {
                Pusher.Remove();
                Pusher = null;
            }
            body = null; 
        }

        public override XElement Save(XElement parentElement)
        {
            if (!attachable)
            {
                return base.Save(parentElement);
            }

            var tempMsg = DisplayMsg;
            var tempRequiredItems = requiredItems;

            DisplayMsg = prevMsg;
            requiredItems = prevRequiredItems;
            
            XElement saveElement = base.Save(parentElement);

            DisplayMsg = tempMsg;
            requiredItems = tempRequiredItems;

            return saveElement;
        }       

    }
}
